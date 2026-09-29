using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Autodesk.Revit.DB;

namespace RevitMCPCommandSet.Services.DataExtraction.Register
{
    /// <summary>
    ///     Produces stable, deterministic document and element keys so that
    ///     register responses can be diffed across sessions and so cursors
    ///     can detect when the source document has changed.
    /// </summary>
    /// <remarks>
    ///     The document key is a SHA-256 hex digest of the canonical path:
    ///     <list type="bullet">
    ///         <item>document title when the file is unsaved (path is null),</item>
    ///         <item>absolute path on disk otherwise,</item>
    ///         <item>prefixed with the host/link tag so host and link documents
    ///             cannot collide.</item>
    ///     </list>
    ///     The element key is composed of the document key and the Revit
    ///     unique id, separated by a colon. Linked instances get an extra
    ///     segment carrying the link instance unique id.
    /// </remarks>
    public static class RegisterKeyProvider
    {
        /// <summary>
        ///     Canonical document key for the host document. Returns "host" when
        ///     <paramref name="documentPath"/> is null/empty (the unsaved case)
        ///     and the SHA-256 hex digest otherwise.
        /// </summary>
        public static string HostDocumentKey(string documentPath, string documentTitle)
        {
            var canonical = string.IsNullOrWhiteSpace(documentPath)
                ? $"unsaved:{documentTitle}"
                : $"path:{documentPath.Trim()}";
            return ComputeHex(canonical);
        }

        /// <summary>
        ///     Document key for a Revit link instance. The link instance unique
        ///     id is mixed into the hash so two hosts that load the same link
        ///     get distinct keys.
        /// </summary>
        public static string LinkedDocumentKey(
            string hostDocumentKey,
            string linkInstanceUniqueId,
            string linkTitle)
        {
            if (string.IsNullOrEmpty(linkInstanceUniqueId))
            {
                throw new ArgumentException("Link instance unique id is required.", nameof(linkInstanceUniqueId));
            }
            var canonical = $"link:{hostDocumentKey}|{linkInstanceUniqueId}|{linkTitle}";
            return ComputeHex(canonical);
        }

        /// <summary>
        ///     Element key for an element in the host document.
        /// </summary>
        public static string HostElementKey(string hostDocumentKey, string elementUniqueId)
        {
            return $"{hostDocumentKey}:{elementUniqueId}";
        }

        /// <summary>
        ///     Element key for an element coming from a linked instance.
        /// </summary>
        public static string LinkedElementKey(
            string hostDocumentKey,
            string linkInstanceUniqueId,
            string elementUniqueId)
        {
            return $"{hostDocumentKey}:link:{linkInstanceUniqueId}:{elementUniqueId}";
        }

        /// <summary>
        ///     Convenience helper: pull both keys from a Revit document.
        /// </summary>
        public static (string DocumentKey, string Title) ResolveHost(Document document)
        {
            if (document == null) throw new ArgumentNullException(nameof(document));
            string path = null;
            try
            {
                path = document.PathName;
            }
            catch (InvalidOperationException)
            {
                // Document is detached or unsaved; treat as no path.
                path = null;
            }
            return (HostDocumentKey(path, document.Title), document.Title ?? string.Empty);
        }

        private static string ComputeHex(string canonical)
        {
            var bytes = Encoding.UTF8.GetBytes(canonical);
            using var sha = SHA256.Create();
            var hash = sha.ComputeHash(bytes);
            var sb = new StringBuilder(hash.Length * 2);
            for (int i = 0; i < hash.Length; i++)
            {
                sb.Append(hash[i].ToString("x2", CultureInfo.InvariantCulture));
            }
            return sb.ToString();
        }
    }
}