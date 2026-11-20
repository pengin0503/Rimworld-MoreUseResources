#!/usr/bin/env bash
# Builds and runs the MoreUseResources Linux benchmark on a VM (no game, no GPU).
#
#   tools/benchmark/run-gcp-benchmark.sh smoke                     ~5-15 min, generated data
#   tools/benchmark/run-gcp-benchmark.sh standard --data ~/mur-data
#   tools/benchmark/run-gcp-benchmark.sh stress --data ~/mur-data --cpus 8
#   tools/benchmark/run-gcp-benchmark.sh soak --hours 4 --data ~/mur-data
#   tools/benchmark/run-gcp-benchmark.sh standard --gc-variants default,server,conserve
#   tools/benchmark/run-gcp-benchmark.sh analyze RESULT_DIR
#   tools/benchmark/run-gcp-benchmark.sh compare BASELINE_DIR OTHER_DIR
#   tools/benchmark/run-gcp-benchmark.sh test                      unit tests only
#
# Options (all optional):
#   --data DIR            real data from fetch-r2-dataset.py (default: generated data). When DIR
#                         does not exist or its download did not finish and R2_ENDPOINT is set,
#                         the data is downloaded first (resuming a partial download)
#   --fetch-profile P     what that download takes: smoke, game (default) or all
#   --out DIR             results root (default: ~/mur-bench-results)
#   --cpus N|LIST         run on N CPUs (0..N-1) or a taskset list such as 0-3,8-11
#   --workers LIST        worker counts, e.g. 1,2,4,8,16
#   --trials N            measured trials per condition
#   --warmup N            warm-up trials per condition
#   --scenarios LIST      e.g. audit,scan,prefetch (default: all)
#   --ram-budget-mib N    RAM the mod may hold for copies and packs
#   --max-files N         at most N input files (stable subset)
#   --max-mib N           at most N MiB of input files
#   --max-runtime-min N   stop after N minutes (results so far are kept)
#   --hours H             soak duration
#   --gc-variants LIST    run once per GC setting (default, server, conserve, concurrent-off)
#                         and compare each with the first
#   --root-cold           run as root and drop the whole page cache for cold conditions
#                         (adds discovery with cold metadata; chmod faults are then not counted)
#   --tests               run the unit tests before measuring
#   --label TEXT          added to the result directory name
#   -- ARGS               passed to the benchmark unchanged
#
# The run stops itself at its time limit and on Ctrl+C / SIGTERM after finishing the current trial;
# everything measured so far stays in the result directory. Exit code: 0 ok, 3 interrupted,
# 4 safety limit, 5 some trials failed, other values errors.
set -euo pipefail

repo=$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)
bench_proj="$repo/Benchmarks/MoreUseResources.Benchmarks.csproj"
bench_dll="$repo/Benchmarks/bin/Release/net10.0/MoreUseResources.Benchmarks.dll"

usage() { sed -n '2,39p' "${BASH_SOURCE[0]}"; }

[ $# -ge 1 ] || { usage; exit 2; }
case "$1" in -h|--help) usage; exit 0 ;; esac
command=$1
shift

data="" out="$HOME/mur-bench-results" cpus="" workers="" trials="" warmup="" scenarios="" budget=""
max_files="" max_mib="" max_runtime="" hours="" gc_variants="default" root_cold=0 run_tests=0 label=""
fetch_profile="game"
positional=() extra=()
while [ $# -gt 0 ]; do
  case "$1" in
    --data) data=$(realpath "$2"); shift 2 ;;
    --out) out=$2; shift 2 ;;
    --cpus) cpus=$2; shift 2 ;;
    --workers) workers=$2; shift 2 ;;
    --trials) trials=$2; shift 2 ;;
    --warmup) warmup=$2; shift 2 ;;
    --scenarios) scenarios=$2; shift 2 ;;
    --ram-budget-mib) budget=$2; shift 2 ;;
    --max-files) max_files=$2; shift 2 ;;
    --max-mib) max_mib=$2; shift 2 ;;
    --max-runtime-min) max_runtime=$2; shift 2 ;;
    --hours) hours=$2; shift 2 ;;
    --gc-variants) gc_variants=$2; shift 2 ;;
    --root-cold) root_cold=1; shift ;;
    --tests) run_tests=1; shift ;;
    --label) label=$2; shift 2 ;;
    --fetch-profile) fetch_profile=$2; shift 2 ;;
    -h|--help) usage; exit 0 ;;
    --) shift; extra=("$@"); break ;;
    -*) echo "unknown option: $1" >&2; exit 2 ;;
    *) positional+=("$1"); shift ;;
  esac
done

case "$fetch_profile" in smoke|game|all) ;; *) echo "--fetch-profile takes smoke, game or all" >&2; exit 2 ;; esac

# Real data is checked, and downloaded when missing, before anything is built: a run must not stop
# after the build because its input is absent.
if [ -n "$data" ] && [ "$command" != "analyze" ] && [ "$command" != "compare" ] && [ "$command" != "test" ]; then
  incomplete=0
  [ -d "$data" ] || incomplete=1
  if [ -f "$data/.r2-download-state.json" ] && [ ! -f "$data/r2-dataset.json" ]; then incomplete=1; fi
  if [ "$incomplete" -eq 1 ]; then
    if [ -z "${R2_ENDPOINT:-}" ]; then
      echo "data directory not found or incomplete: $data" >&2
      echo "set R2_ENDPOINT, AWS_ACCESS_KEY_ID and AWS_SECRET_ACCESS_KEY and rerun to download it," >&2
      echo "or download it yourself: python3 -I tools/benchmark/fetch-r2-dataset.py $data --profile $fetch_profile" >&2
      echo "or leave out --data to measure on generated data" >&2
      exit 2
    fi
    echo "downloading R2 data (profile $fetch_profile) into $data before measuring"
    if ! python3 -I "$repo/tools/benchmark/fetch-r2-dataset.py" "$data" --profile "$fetch_profile"; then
      echo "the download failed (message above); nothing was measured. Rerun the same command to resume it." >&2
      exit 1
    fi
  fi
fi

dotnet_bin=$(command -v dotnet) || { echo "dotnet not found: run tools/benchmark/setup-ubuntu.sh first" >&2; exit 1; }
mkdir -p "$out"
out=$(realpath "$out")

build() {
  echo "building the benchmark (Release)"
  dotnet build "$bench_proj" -c Release -nologo -v quiet
}

tests() {
  echo "unit tests: benchmark"
  dotnet test "$repo/Benchmarks/Tests/MoreUseResources.Benchmarks.Tests.csproj" -c Release -nologo -v quiet
  echo "unit tests: mod"
  dotnet test "$repo/Tests/MoreUseResources.Tests.csproj" -c Release -nologo -v quiet
}

bench() { "$dotnet_bin" "$bench_dll" "$@"; }

case "$command" in
  test) tests; exit 0 ;;
  analyze)
    [ ${#positional[@]} -ge 1 ] || { echo "analyze needs a result directory" >&2; exit 2; }
    build; bench analyze "${positional[@]}"; exit $? ;;
  compare)
    [ ${#positional[@]} -eq 2 ] || { echo "compare needs two result directories" >&2; exit 2; }
    build; bench compare "${positional[@]}"; exit $? ;;
  smoke|standard|stress|soak) ;;
  *) echo "unknown command: $command" >&2; usage; exit 2 ;;
esac

build
[ "$run_tests" -eq 1 ] && tests

args=()
if [ "$command" = "soak" ]; then
  args+=(soak --mode standard --duration-min "$(awk -v h="${hours:-4}" 'BEGIN { printf "%d", h * 60 }')")
else
  args+=(run --mode "$command")
fi
args+=(--out "$out")
[ -n "$data" ] && args+=(--data "$data") || args+=(--generate "$out/generated-data")
[ -n "$workers" ] && args+=(--workers "$workers")
[ -n "$trials" ] && args+=(--trials "$trials")
[ -n "$warmup" ] && args+=(--warmup "$warmup")
[ -n "$scenarios" ] && args+=(--scenarios "$scenarios")
[ -n "$budget" ] && args+=(--ram-budget-mib "$budget")
[ -n "$max_files" ] && args+=(--max-files "$max_files")
[ -n "$max_mib" ] && args+=(--max-mib "$max_mib")
[ -n "$max_runtime" ] && args+=(--max-runtime-min "$max_runtime")
[ "$root_cold" -eq 1 ] && args+=(--cold-method drop-caches)

# An outer limit in case the process stops responding: the benchmark's own limit plus a margin.
case "$command" in
  smoke) limit_min=${max_runtime:-20} ;;
  standard) limit_min=${max_runtime:-360} ;;
  stress) limit_min=${max_runtime:-600} ;;
  soak) limit_min=$(awk -v h="${hours:-4}" 'BEGIN { printf "%d", h * 60 + 30 }') ;;
esac
limit_min=$(awk -v m="$limit_min" 'BEGIN { printf "%d", m + 15 }')

prefix=()
if [ -n "$cpus" ]; then
  if [[ "$cpus" =~ ^[0-9]+$ ]]; then cpulist="0-$((cpus - 1))"; else cpulist=$cpus; fi
  prefix+=(taskset -c "$cpulist")
  echo "pinned to CPUs $cpulist (the benchmark then sees $(taskset -c "$cpulist" nproc) logical processors)"
fi
if [ "$root_cold" -eq 1 ]; then
  prefix=(sudo --preserve-env=PATH,HOME,DOTNET_ROOT,DOTNET_CLI_HOME,DOTNET_gcServer,DOTNET_GCConserveMemory,DOTNET_gcConcurrent "${prefix[@]}")
fi

gc_env() {
  case "$1" in
    default) echo "" ;;
    server) echo "DOTNET_gcServer=1" ;;
    conserve) echo "DOTNET_GCConserveMemory=5" ;;
    concurrent-off) echo "DOTNET_gcConcurrent=0" ;;
    *) echo "unknown GC variant: $1 (default, server, conserve, concurrent-off)" >&2; exit 2 ;;
  esac
}

IFS=',' read -r -a variants <<< "$gc_variants"
status=0
dirs=()
for variant in "${variants[@]}"; do
  envs=$(gc_env "$variant")
  run_label=$label
  if [ ${#variants[@]} -gt 1 ]; then run_label="${label:+$label-}gc-$variant"; fi
  label_args=()
  [ -n "$run_label" ] && label_args=(--label "$run_label")
  before=$(ls -1 "$out" 2>/dev/null | sort)
  echo "== $command${envs:+ with $envs} (outer limit ${limit_min} min)"
  set +e
  # shellcheck disable=SC2086
  env $envs timeout --signal=INT --kill-after=10m "${limit_min}m" \
    "${prefix[@]}" "$dotnet_bin" "$bench_dll" "${args[@]}" "${label_args[@]}" "${extra[@]}"
  code=$?
  set -e
  [ $code -eq 124 ] && code=3 # outer timeout: the run was interrupted, results are kept
  [ $code -ne 0 ] && [ $status -eq 0 ] && status=$code
  new=$(comm -13 <(printf '%s\n' "$before") <(ls -1 "$out" | sort) | grep -v '^generated-data$' | tail -1 || true)
  [ -n "$new" ] && dirs+=("$out/$new")
  if [ "$root_cold" -eq 1 ]; then sudo chown -R "$(id -u):$(id -g)" "$out"; fi
  echo "exit code $code${new:+, results in $out/$new}"
done

if [ ${#dirs[@]} -gt 1 ]; then
  for other in "${dirs[@]:1}"; do
    echo "== comparing $(basename "${dirs[0]}") with $(basename "$other")"
    bench compare "${dirs[0]}" "$other" || true
  done
fi
for d in "${dirs[@]}"; do echo "analysis: $d/analysis.md"; done
exit $status
