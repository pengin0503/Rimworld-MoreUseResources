using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;

namespace MoreUseResources.Core
{
    /// <summary>Read-only results from examining enabled mods' real asset files.</summary>
    public readonly struct AssetAuditSnapshot
    {
        public readonly int Sources;
        public readonly int Completed;
        public readonly int Duplicates;
        public readonly long DuplicateBytes;
        public readonly long HashedBytes;
        public readonly int Errors;
        public readonly int Skipped;
        public readonly bool Complete;
        public readonly int PassesCompleted;
        public readonly long ChangedFiles;
        public readonly int XmlFilesChecked;
        public readonly int XmlParseFailures;
        public readonly int XmlDefinitions;
        public readonly int XmlDefinitionCollisions;
        public readonly int XmlFilesSkipped;
        public readonly int XmlKeysOmitted;

        internal AssetAuditSnapshot(int sources, int completed, int duplicates, long duplicateBytes,
            long hashedBytes, int errors, int skipped, bool complete, int passesCompleted, long changedFiles,
            int xmlFilesChecked, int xmlParseFailures, int xmlDefinitions, int xmlDefinitionCollisions,
            int xmlFilesSkipped, int xmlKeysOmitted)
        {
            Sources = sources;
            Completed = completed;
            Duplicates = duplicates;
            DuplicateBytes = duplicateBytes;
            HashedBytes = hashedBytes;
            Errors = errors;
            Skipped = skipped;
            Complete = complete;
            PassesCompleted = passesCompleted;
            ChangedFiles = changedFiles;
            XmlFilesChecked = xmlFilesChecked;
            XmlParseFailures = xmlParseFailures;
            XmlDefinitions = xmlDefinitions;
            XmlDefinitionCollisions = xmlDefinitionCollisions;
            XmlFilesSkipped = xmlFilesSkipped;
            XmlKeysOmitted = xmlKeysOmitted;
        }
    }

    /// <summary>
    /// Uses otherwise idle workers to fingerprint real Def, patch, language, texture and sound files.
    /// A content index identifies exact duplicate assets across enabled mods. It never touches
    /// RimWorld objects, alters files or patches gameplay methods. Files may be modified while
    /// the game is running; a changed file is retried rather than indexed from a torn read.
    /// </summary>
    public sealed class AssetAudit
    {
        private const int BufferBytes = 128 * 1024;
        private const long MaxFileBytes = 1L << 30;
        private const int MaxXmlIndexKeys = 250_000;
        private readonly WorkerPool _pool;
        private readonly Func<bool>? _mayRun;
        private readonly Func<long> _clockMs;
        private readonly object _gate = new object();
        private readonly HashSet<string> _completed;
        private readonly Dictionary<string, string> _firstByHash = new Dictionary<string, string>(StringComparer.Ordinal);
        private readonly Dictionary<string, string> _lastHashByPath;
        private readonly HashSet<string> _xmlKeys = new HashSet<string>(StringComparer.Ordinal);
        private List<string> _files = new List<string>();
        private int _cursor;
        private int _inFlight;
        private int _generation;
        private bool _active;
        private bool _retry;
        private long _hashedBytes;
        private long _duplicateBytes;
        private int _duplicates;
        private int _errors;
        private int _skipped;
        private int _passesCompleted;
        private long _changedFiles;
        private long _passCompletedAtMs;
        private bool _passRecorded;
        private int _xmlFilesChecked;
        private int _xmlParseFailures;
        private int _xmlDefinitions;
        private int _xmlDefinitionCollisions;
        private int _xmlFilesSkipped;
        private int _xmlKeysOmitted;

        public AssetAudit(WorkerPool pool, Func<bool>? mayRun = null, Func<long>? clockMs = null)
        {
            _pool = pool;
            _mayRun = mayRun;
            _clockMs = clockMs ?? (() => Environment.TickCount64);
            var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
            _completed = new HashSet<string>(pathComparer);
            _lastHashByPath = new Dictionary<string, string>(pathComparer);
        }

        public AssetAuditSnapshot Snapshot
        {
            get
            {
                lock (_gate)
                {
                    return new AssetAuditSnapshot(_files.Count, _completed.Count, _duplicates,
                        _duplicateBytes, _hashedBytes, _errors, _skipped,
                        _cursor >= _files.Count && _inFlight == 0 && !_retry && _completed.Count == _files.Count,
                        _passesCompleted, _changedFiles, _xmlFilesChecked, _xmlParseFailures,
                        _xmlDefinitions, _xmlDefinitionCollisions, _xmlFilesSkipped, _xmlKeysOmitted);
                }
            }
        }

        public void SetSources(IEnumerable<string> paths)
        {
            var unique = new HashSet<string>(OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            var list = new List<string>();
            foreach (string path in paths)
                if (!string.IsNullOrWhiteSpace(path) && unique.Add(path)) list.Add(path);
            lock (_gate)
            {
                _generation++;
                _active = false;
                _files = list;
                _cursor = 0;
                _retry = false;
                _completed.Clear();
                _firstByHash.Clear();
                _lastHashByPath.Clear();
                ResetXmlResults();
                _passesCompleted = 0;
                _changedFiles = 0;
                _passRecorded = false;
                _passCompletedAtMs = 0;
                _hashedBytes = 0;
                _duplicateBytes = 0;
                _duplicates = 0;
                _errors = 0;
                _skipped = 0;
                // Old-generation jobs retain their in-flight count until they exit.
            }
        }

        public void Suspend()
        {
            lock (_gate) { _active = false; }
        }

        /// <summary>Called by the controller after its phase, frame and memory guards.</summary>
        public void Pump(bool active, int workers, long rescanIntervalMs = 0)
        {
            int limit = Math.Clamp(workers * 2, 1, 32);
            lock (_gate)
            {
                _active = active;
                if (!active) return;

                bool finished = _cursor >= _files.Count && _inFlight == 0 && !_retry
                    && _completed.Count == _files.Count;
                if (finished && !_passRecorded)
                {
                    _passRecorded = true;
                    _passesCompleted++;
                    _passCompletedAtMs = _clockMs();
                }
                // Each new pass rehashes real files; only the previous fingerprints survive.
                // Rescans are explicitly bounded by the user-configured interval.
                if (_passRecorded && rescanIntervalMs > 0
                    && _clockMs() - _passCompletedAtMs >= rescanIntervalMs)
                {
                    _passRecorded = false;
                    _cursor = 0;
                    _retry = false;
                    _completed.Clear();
                    _firstByHash.Clear();
                    _hashedBytes = 0;
                    _duplicateBytes = 0;
                    _duplicates = 0;
                    _errors = 0;
                    _skipped = 0;
                    ResetXmlResults();
                }
                if (_cursor >= _files.Count && _inFlight == 0 && _retry)
                {
                    _cursor = 0;
                    _retry = false;
                }
            }
            while (true)
            {
                string path;
                int generation;
                lock (_gate)
                {
                    if (!_active || _inFlight >= limit || _cursor >= _files.Count) return;
                    path = _files[_cursor++];
                    if (_completed.Contains(path)) continue;
                    generation = _generation;
                    _inFlight++;
                }
                _pool.Enqueue(token => ProcessFile(path, generation, token));
            }
        }

        /// <summary>Only called while _gate is held.</summary>
        private void ResetXmlResults()
        {
            _xmlKeys.Clear();
            _xmlFilesChecked = 0;
            _xmlParseFailures = 0;
            _xmlDefinitions = 0;
            _xmlDefinitionCollisions = 0;
            _xmlFilesSkipped = 0;
            _xmlKeysOmitted = 0;
        }

        /// <summary>Only called while _gate is held, after source generation and metadata checks.</summary>
        private void RecordXml(XmlManifestReport report)
        {
            _xmlFilesChecked++;
            if (report.ParseFailed) { _xmlParseFailures++; return; }
            if (report.Skipped) { _xmlFilesSkipped++; return; }
            _xmlDefinitions += report.DefinitionElements;
            foreach (string name in report.DefinitionKeys)
            {
                if (_xmlKeys.Contains(name)) { _xmlDefinitionCollisions++; continue; }
                if (_xmlKeys.Count < MaxXmlIndexKeys) _xmlKeys.Add(name);
                else _xmlKeysOmitted++;
            }
        }

        private bool MayRun(int generation, CancellationToken token) =>
            !token.IsCancellationRequested && Volatile.Read(ref _active)
            && generation == Volatile.Read(ref _generation) && (_mayRun?.Invoke() ?? true);

        private void ProcessFile(string path, int generation, CancellationToken token)
        {
            bool completed = false;
            try
            {
                if (!MayRun(generation, token)) return;
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete, BufferBytes, FileOptions.SequentialScan);
                long expectedLength = stream.Length;
                DateTime expectedWrite = File.GetLastWriteTimeUtc(path);
                if (expectedLength > MaxFileBytes)
                {
                    lock (_gate)
                    {
                        if (generation == _generation && _active)
                        {
                            _skipped++;
                            _completed.Add(path);
                            completed = true;
                        }
                    }
                    return;
                }
                using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferBytes);
                long size = 0;
                try
                {
                    int count;
                    while (MayRun(generation, token) && (count = stream.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        size += count;
                        if (size > MaxFileBytes) return;
                        hash.AppendData(buffer, 0, count);
                    }
                }
                finally
                {
                    ArrayPool<byte>.Shared.Return(buffer);
                }
                if (!MayRun(generation, token) || size != expectedLength
                    || File.GetLastWriteTimeUtc(path) != expectedWrite) return;
                string fingerprint = size.ToString("X") + ":" + Convert.ToHexString(hash.GetHashAndReset());
                XmlManifestReport? xml = null;
                if (string.Equals(Path.GetExtension(path), ".xml", StringComparison.OrdinalIgnoreCase))
                {
                    xml = XmlManifestScanner.Scan(path, token);
                    // The file may have changed while the XML reader was inspecting it.
                    if (!MayRun(generation, token) || stream.Length != expectedLength
                        || File.GetLastWriteTimeUtc(path) != expectedWrite) return;
                }
                lock (_gate)
                {
                    if (!MayRun(generation, token)) return;
                    if (_completed.Add(path))
                    {
                        if (_lastHashByPath.TryGetValue(path, out string? previous)
                            && !StringComparer.Ordinal.Equals(previous, fingerprint))
                            _changedFiles++;
                        _lastHashByPath[path] = fingerprint;
                        _hashedBytes += size;
                        if (xml.HasValue) RecordXml(xml.Value);
                        if (_firstByHash.ContainsKey(fingerprint))
                        {
                            _duplicates++;
                            _duplicateBytes += size;
                        }
                        else _firstByHash.Add(fingerprint, path);
                    }
                    completed = true;
                }
            }
            catch (Exception ex) when (ex is IOException || ex is UnauthorizedAccessException
                || ex is NotSupportedException || ex is ArgumentException
                || ex is System.Security.SecurityException)
            {
                lock (_gate)
                {
                    if (generation == _generation && _active)
                    {
                        _errors++;
                        _completed.Add(path);
                        completed = true;
                    }
                }
            }
            finally
            {
                lock (_gate)
                {
                    _inFlight--;
                    if (!completed && generation == _generation) _retry = true;
                }
            }
        }
    }
}
