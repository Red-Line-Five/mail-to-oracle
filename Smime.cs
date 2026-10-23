using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.Pkcs;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace OwaToOracle
{
    /// <summary>A very small MIME entity (headers + raw body bytes).</summary>
    public class MimeEntity
    {
        public Dictionary<string, string> H = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        public byte[] Body = new byte[0];

        public string Get(string name) { string v; return H.TryGetValue(name, out v) ? v : ""; }

        public string Type
        {
            get
            {
                string ct = Get("Content-Type");
                int i = ct.IndexOf(';');
                string t = (i >= 0 ? ct.Substring(0, i) : ct).Trim().ToLowerInvariant();
                return t.Length == 0 ? "text/plain" : t;
            }
        }

        public string Param(string name)
        {
            Match m = Regex.Match(Get("Content-Type"), "[;\\s]" + Regex.Escape(name) + "\\s*=\\s*(\"([^\"]*)\"|([^;\\s]+))", RegexOptions.IgnoreCase);
            if (!m.Success) return "";
            return m.Groups[2].Success ? m.Groups[2].Value : m.Groups[3].Value;
        }
    }

    public class SmimeResult
    {
        public bool Encrypted, Signed, SigValid;
        public X509Certificate2 Signer;
        public string Text = "";
        public string Error;
    }

    /// <summary>
    /// S/MIME with built-in .NET classes only (System.Security.Cryptography.Pkcs):
    /// decrypt incoming mail with MY private key, verify the sender's signature,
    /// and build an encrypted + signed reply for the sender's public key.
    /// </summary>
    public static class Smime
    {
        private static readonly Encoding L1 = Encoding.GetEncoding(28591);   // bytes <-> chars 1:1

        public static string TrustedDir { get { return Path.Combine(Settings.Dir, "trusted"); } }

        // ------------------------------------------------------------ certificates
        public static X509Certificate2 FindMyCert(string thumbprint)
        {
            if (string.IsNullOrWhiteSpace(thumbprint)) return null;
            string t = Regex.Replace(thumbprint, "[^0-9A-Fa-f]", "");
            using (var st = new X509Store("My", StoreLocation.CurrentUser))
            {
                st.Open(OpenFlags.ReadOnly);
                X509Certificate2Collection col = st.Certificates.Find(X509FindType.FindByThumbprint, t, false);
                return col.Count > 0 ? col[0] : null;
            }
        }

        /// <summary>Returns the certificate from the trusted folder that matches the signer (same thumbprint, not expired).</summary>
        public static X509Certificate2 FindTrusted(X509Certificate2 signer)
        {
            if (signer == null || !Directory.Exists(TrustedDir)) return null;
            foreach (string f in Directory.GetFiles(TrustedDir))
            {
                try
                {
                    var c = new X509Certificate2(f);
                    if (string.Equals(c.Thumbprint, signer.Thumbprint, StringComparison.OrdinalIgnoreCase) &&
                        c.NotBefore <= DateTime.Now && c.NotAfter >= DateTime.Now)
                        return c;
                }
                catch { }
            }
            return null;
        }

        // ------------------------------------------------------------ MIME helpers
        public static MimeEntity Parse(byte[] data)
        {
            string s = L1.GetString(data);
            int sep = s.IndexOf("\r\n\r\n", StringComparison.Ordinal), sepLen = 4;
            int sep2 = s.IndexOf("\n\n", StringComparison.Ordinal);
            if (sep < 0 || (sep2 >= 0 && sep2 < sep)) { sep = sep2; sepLen = 2; }

            var e = new MimeEntity();
            string head = sep >= 0 ? s.Substring(0, sep) : s;
            int bodyStart = sep >= 0 ? sep + sepLen : s.Length;
            e.Body = new byte[Math.Max(0, data.Length - bodyStart)];
            Array.Copy(data, bodyStart, e.Body, 0, e.Body.Length);

            string cur = null;
            foreach (string line in head.Replace("\r\n", "\n").Split('\n'))
            {
                if (line.Length > 0 && (line[0] == ' ' || line[0] == '\t') && cur != null) { cur += " " + line.Trim(); continue; }
                AddHeader(e, cur);
                cur = line;
            }
            AddHeader(e, cur);
            return e;
        }

        private static void AddHeader(MimeEntity e, string line)
        {
            if (string.IsNullOrEmpty(line)) return;
            int i = line.IndexOf(':');
            if (i <= 0) return;
            string k = line.Substring(0, i).Trim();
            if (!e.H.ContainsKey(k)) e.H[k] = line.Substring(i + 1).Trim();
        }

        public static byte[] Decode(MimeEntity e)
        {
            string cte = e.Get("Content-Transfer-Encoding").Trim().ToLowerInvariant();
            if (cte == "base64")
                return Convert.FromBase64String(Regex.Replace(L1.GetString(e.Body), "[^A-Za-z0-9+/=]", ""));
            if (cte == "quoted-printable") return QpDecode(e.Body);
            return e.Body;
        }

        private static int Hex(byte b)
        {
            if (b >= '0' && b <= '9') return b - '0';
            if (b >= 'A' && b <= 'F') return b - 'A' + 10;
            if (b >= 'a' && b <= 'f') return b - 'a' + 10;
            return -1;
        }

        private static byte[] QpDecode(byte[] input)
        {
            var o = new MemoryStream();
            for (int i = 0; i < input.Length; i++)
            {
                byte c = input[i];
                if (c == '=')
                {
                    if (i + 1 < input.Length && input[i + 1] == '\n') { i += 1; continue; }
                    if (i + 2 < input.Length && input[i + 1] == '\r' && input[i + 2] == '\n') { i += 2; continue; }
                    if (i + 2 < input.Length)
                    {
                        int h = Hex(input[i + 1]), l = Hex(input[i + 2]);
                        if (h >= 0 && l >= 0) { o.WriteByte((byte)(h * 16 + l)); i += 2; continue; }
                    }
                }
                o.WriteByte(c);
            }
            return o.ToArray();
        }

        /// <summary>Splits a multipart body; every part is returned as raw bytes (headers included).</summary>
        public static List<byte[]> SplitMultipart(byte[] body, string boundary)
        {
            var parts = new List<byte[]>();
            if (string.IsNullOrEmpty(boundary)) return parts;
            string s = L1.GetString(body);
            string delim = "--" + boundary;
            int pos = s.IndexOf(delim, StringComparison.Ordinal);
            while (pos >= 0)
            {
                int after = pos + delim.Length;
                if (after + 1 < s.Length && string.CompareOrdinal(s, after, "--", 0, 2) == 0) break;   // closing delimiter
                int lineEnd = s.IndexOf('\n', after);
                if (lineEnd < 0) break;
                int start = lineEnd + 1;
                int next = s.IndexOf("\n" + delim, lineEnd, StringComparison.Ordinal);
                if (next < 0) { parts.Add(L1.GetBytes(s.Substring(start))); break; }
                int end = next;
                if (end > start && s[end - 1] == '\r') end--;
                parts.Add(end > start ? L1.GetBytes(s.Substring(start, end - start)) : new byte[0]);
                pos = next + 1;
            }
            return parts;
        }

        private static string StripHtml(string html)
        {
            string t = Regex.Replace(html, @"<(script|style)[\s\S]*?</\1>", "", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, @"<br\s*/?>|</p>|</div>|</tr>", "\n", RegexOptions.IgnoreCase);
            t = Regex.Replace(t, "<[^>]+>", "");
            return WebUtility.HtmlDecode(t);
        }

        public static string TextOf(MimeEntity e)
        {
            string t = e.Type;
            if (t.StartsWith("multipart/"))
            {
                string html = null;
                foreach (byte[] raw in SplitMultipart(e.Body, e.Param("boundary")))
                {
                    MimeEntity pe = Parse(raw);
                    if (pe.Get("Content-Disposition").TrimStart().StartsWith("attachment", StringComparison.OrdinalIgnoreCase)) continue;
                    string pt = pe.Type;
                    if (pt == "text/plain" || pt.StartsWith("multipart/"))
                    {
                        string x = TextOf(pe);
                        if (x.Length > 0) return x;
                    }
                    else if (pt == "text/html" && html == null) html = TextOf(pe);
                }
                return html ?? "";
            }
            if (t == "text/plain" || t == "text/html")
            {
                Encoding enc;
                try { enc = Encoding.GetEncoding(string.IsNullOrEmpty(e.Param("charset")) ? "utf-8" : e.Param("charset")); }
                catch { enc = Encoding.UTF8; }
                string txt = enc.GetString(Decode(e));
                return t == "text/html" ? StripHtml(txt) : txt;
            }
            return "";
        }

        // ------------------------------------------------------------ reading S/MIME mail
        public static bool IsSmime(byte[] mime)
        {
            if (mime == null || mime.Length == 0) return false;
            string t = Parse(mime).Type;
            return t == "application/pkcs7-mime" || t == "application/x-pkcs7-mime" || t == "multipart/signed";
        }

        private static bool Verify(SignedCms sc, SmimeResult r)
        {
            try
            {
                sc.CheckSignature(true);          // true = check the signature itself (trust is decided by the trusted folder)
                if (sc.SignerInfos.Count > 0) r.Signer = sc.SignerInfos[0].Certificate;
                return r.Signer != null;
            }
            catch { return false; }
        }

        /// <summary>Decrypts (needs MY certificate with private key) and/or verifies the signature, returns the readable text.</summary>
        public static SmimeResult Open(byte[] mime, X509Certificate2 mine)
        {
            var r = new SmimeResult();
            try
            {
                MimeEntity e = Parse(mime);
                for (int depth = 0; depth < 5; depth++)          // encrypted( signed( text ) ) etc.
                {
                    string t = e.Type;
                    if (t == "application/pkcs7-mime" || t == "application/x-pkcs7-mime")
                    {
                        byte[] der = Decode(e);
                        if (e.Param("smime-type").ToLowerInvariant() == "signed-data")
                        {
                            var sc = new SignedCms();
                            sc.Decode(der);
                            r.Signed = true;
                            r.SigValid = Verify(sc, r);
                            e = Parse(sc.ContentInfo.Content);
                        }
                        else
                        {
                            if (mine == null) throw new Exception("no certificate selected in Settings - cannot decrypt");
                            var ec = new EnvelopedCms();
                            ec.Decode(der);
                            ec.Decrypt(new X509Certificate2Collection(mine));
                            r.Encrypted = true;
                            e = Parse(ec.ContentInfo.Content);
                        }
                        continue;
                    }
                    if (t == "multipart/signed")
                    {
                        List<byte[]> parts = SplitMultipart(e.Body, e.Param("boundary"));
                        if (parts.Count >= 2)
                        {
                            byte[] sig = Decode(Parse(parts[1]));
                            var sc = new SignedCms(new ContentInfo(parts[0]), true);
                            sc.Decode(sig);
                            r.Signed = true;
                            r.SigValid = Verify(sc, r);
                            e = Parse(parts[0]);
                            continue;
                        }
                    }
                    break;
                }
                r.Text = TextOf(e);
            }
            catch (Exception ex) { r.Error = ex.Message; }
            return r;
        }

        // ------------------------------------------------------------ building an encrypted reply
        private static string B64(byte[] b) { return Convert.ToBase64String(b, Base64FormattingOptions.InsertLineBreaks); }

        private static string EncodeHeader(string s)
        {
            foreach (char c in s)
                if (c > 127) return "=?utf-8?B?" + Convert.ToBase64String(Encoding.UTF8.GetBytes(s)) + "?=";
            return s;
        }

        /// <summary>text -> signed with MY key -> encrypted for the sender's public key (AES-256). Returns the complete MIME mail.</summary>
        public static byte[] BuildReply(string to, string subject, string text, X509Certificate2 mine, X509Certificate2 recipient)
        {
            // 1) the readable part
            string inner = "Content-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: base64\r\n\r\n" +
                           B64(Encoding.UTF8.GetBytes(text));

            // 2) sign it (so the receiver knows the answer really comes from me)
            var sc = new SignedCms(new ContentInfo(Encoding.ASCII.GetBytes(inner)), false);
            var signer = new CmsSigner(SubjectIdentifierType.IssuerAndSerialNumber, mine);
            signer.IncludeOption = X509IncludeOption.EndCertOnly;
            signer.DigestAlgorithm = new Oid("2.16.840.1.101.3.4.2.1");     // SHA-256
            sc.ComputeSignature(signer);
            string signedEntity = "Content-Type: application/pkcs7-mime; smime-type=signed-data; name=\"smime.p7m\"\r\n" +
                                  "Content-Transfer-Encoding: base64\r\nContent-Disposition: attachment; filename=\"smime.p7m\"\r\n\r\n" +
                                  B64(sc.Encode());

            // 3) encrypt for the sender's public key
            var ec = new EnvelopedCms(new ContentInfo(Encoding.ASCII.GetBytes(signedEntity)),
                                      new AlgorithmIdentifier(new Oid("2.16.840.1.101.3.4.1.42")));   // AES-256-CBC
            ec.Encrypt(new CmsRecipient(SubjectIdentifierType.IssuerAndSerialNumber, recipient));

            string msg = "MIME-Version: 1.0\r\nTo: " + to + "\r\nSubject: " + EncodeHeader(subject) + "\r\n" +
                         "Content-Type: application/pkcs7-mime; smime-type=enveloped-data; name=\"smime.p7m\"\r\n" +
                         "Content-Transfer-Encoding: base64\r\nContent-Disposition: attachment; filename=\"smime.p7m\"\r\n\r\n" +
                         B64(ec.Encode());
            return Encoding.ASCII.GetBytes(msg);
        }
    }
}
