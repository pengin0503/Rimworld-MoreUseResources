using System;
using System.IO;
using System.Xml;

namespace MoreUseResources.Core;

/// <summary>Preserves node boundaries and values that an XML text round trip can normalize.</summary>
public static class XmlDomCodec
{
    private const int MaxDepth = 128;
    private const int MaxNodes = 2_000_000;
    private const int MaxCharacters = 32 * 1024 * 1024;

    public static void Write(BinaryWriter writer, XmlNode node) => new Limits().WriteNode(writer, node, 0);
    public static XmlNode Read(BinaryReader reader, XmlDocument owner) => new Limits().ReadNode(reader, owner, 0);

    private sealed class Limits
    {
        private int nodes;
        private long characters;

        private void AddNode(int depth)
        {
            if (depth >= MaxDepth || ++nodes > MaxNodes) throw new InvalidDataException("XML tree exceeds the cache limit.");
        }

        private string Bound(string value)
        {
            characters += value.Length;
            if (characters > MaxCharacters) throw new InvalidDataException("XML text exceeds the cache limit.");
            return value;
        }

        internal void WriteNode(BinaryWriter writer, XmlNode node, int depth)
        {
            AddNode(depth);
            writer.Write((byte)node.NodeType);
            if (node is XmlElement or XmlAttribute)
            {
                writer.Write(Bound(node.Prefix));
                writer.Write(Bound(node.LocalName));
                writer.Write(Bound(node.NamespaceURI));
                if (node is XmlElement element)
                {
                    writer.Write(element.IsEmpty);
                    writer.Write(element.Attributes.Count);
                    foreach (XmlAttribute attribute in element.Attributes) WriteNode(writer, attribute, depth + 1);
                }
                writer.Write(node.ChildNodes.Count);
                foreach (XmlNode child in node.ChildNodes) WriteNode(writer, child, depth + 1);
                return;
            }

            if (node is XmlProcessingInstruction instruction) writer.Write(Bound(instruction.Target));
            else if (node is not (XmlText or XmlCDataSection or XmlComment or XmlWhitespace or XmlSignificantWhitespace))
                throw new InvalidDataException("Unsupported XML node type.");
            writer.Write(Bound(node.Value ?? ""));
        }

        internal XmlNode ReadNode(BinaryReader reader, XmlDocument owner, int depth)
        {
            AddNode(depth);
            var kind = (XmlNodeType)reader.ReadByte();
            if (kind is XmlNodeType.Element or XmlNodeType.Attribute)
            {
                string prefix = Bound(reader.ReadString());
                string local = Bound(reader.ReadString());
                string ns = Bound(reader.ReadString());
                if (kind == XmlNodeType.Attribute)
                {
                    XmlAttribute attribute = owner.CreateAttribute(prefix, local, ns);
                    ReadChildren(reader, owner, attribute, depth);
                    return attribute;
                }

                XmlElement element = owner.CreateElement(prefix, local, ns);
                bool emptyTag = reader.ReadBoolean();
                int attributes = ReadCount(reader);
                for (int i = 0; i < attributes; i++)
                {
                    if (ReadNode(reader, owner, depth + 1) is not XmlAttribute attribute
                        || element.HasAttribute(attribute.LocalName, attribute.NamespaceURI))
                        throw new InvalidDataException("Invalid cached XML attribute.");
                    element.Attributes.Append(attribute);
                }
                ReadChildren(reader, owner, element, depth);
                if (emptyTag && element.HasChildNodes) throw new InvalidDataException("Conflicting XML element state.");
                element.IsEmpty = emptyTag;
                return element;
            }

            return kind switch
            {
                XmlNodeType.Text => owner.CreateTextNode(Bound(reader.ReadString())),
                XmlNodeType.CDATA => owner.CreateCDataSection(Bound(reader.ReadString())),
                XmlNodeType.Comment => owner.CreateComment(Bound(reader.ReadString())),
                XmlNodeType.Whitespace => owner.CreateWhitespace(Bound(reader.ReadString())),
                XmlNodeType.SignificantWhitespace => owner.CreateSignificantWhitespace(Bound(reader.ReadString())),
                XmlNodeType.ProcessingInstruction => owner.CreateProcessingInstruction(Bound(reader.ReadString()), Bound(reader.ReadString())),
                _ => throw new InvalidDataException("Unsupported cached XML node type.")
            };
        }

        private void ReadChildren(BinaryReader reader, XmlDocument owner, XmlNode parent, int depth)
        {
            int count = ReadCount(reader);
            for (int i = 0; i < count; i++) parent.AppendChild(ReadNode(reader, owner, depth + 1));
        }

        private int ReadCount(BinaryReader reader)
        {
            int count = reader.ReadInt32();
            if (count < 0 || count > MaxNodes - nodes) throw new InvalidDataException("Invalid cached XML node count.");
            return count;
        }
    }
}
