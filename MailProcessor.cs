using System;
using System.Collections.Generic;
using System.Data;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.RegularExpressions;

namespace OwaToOracle
{
    public class Row
    {
        public string Time = "", From = "", A = "", D = "", Status = "", Info = "";
    }

    /// <summary>Remembers which mails were already handled (survives restarts).</summary>
    public static class Store
    {
        private static readonly object L = new object();
        private static HashSet<string> _done;

        private static string FilePath { get { return Path.Combine(Settings.Dir, "processed.tsv"); } }

        private static void Ensure()
        {
            if (_done != null) return;
            _done = new HashSet<string>();
            try
            {
                if (File.Exists(FilePath))
                    foreach (string line in File.ReadAllLines(FilePath))
                    {
                        string[] p = line.Split('\t');
                        if (p.Length > 0 && p[0].Length > 0) _done.Add(p[0]);
                    }
            }
            catch { }
        }

        private static string San(string s) { return (s ?? "").Replace("\t", " ").Replace("\r", " ").Replace("\n", " "); }

        public static bool Has(string hash) { lock (L) { Ensure(); return _done.Contains(hash); } }

        public static void Add(string hash, Row r)
        {
            lock (L)
            {
                Ensure();
                _done.Add(hash);
                Directory.CreateDirectory(Settings.Dir);
                File.AppendAllText(FilePath,
                    string.Join("\t", new[] { hash, r.Time, San(r.From), San(r.A), San(r.D), r.Status, San(r.Info) }) + Environment.NewLine,
                    Encoding.UTF8);
            }
        }

        public static List<Row> Last(int n)
        {
            var rows = new List<Row>();
            try
            {
                if (!File.Exists(FilePath)) return rows;
                foreach (string line in File.ReadAllLines(FilePath).Reverse().Take(n))
                {
                    string[] p = line.Split('\t');
                    if (p.Length < 7) continue;
                    rows.Add(new Row { Time = p[1], From = p[2], A = p[3], D = p[4], Status = p[5], Info = p[6] });
                }
            }
            catch { }
            return rows;       // newest first
        }
    }

    public static class MailProcessor
    {
        private const int MaxAttempts = 3;
        private static readonly Dictionary<string, int> Attempts = new Dictionary<string, int>();

        // A:value  and  D:value   (value = everything up to the next space, comma or semicolon)
        private static readonly Regex RxA = new Regex(@"(?<![A-Za-z0-9_])A[ \t]*:[ \t]*([^\s,;]+)", RegexOptions.IgnoreCase);
        // status:add  or  status:delete
        private static readonly Regex RxS = new Regex(@"(?<![A-Za-z0-9_])status[ \t]*:[ \t]*(add|delete|search)(?![A-Za-z0-9_])", RegexOptions.IgnoreCase);
        private static readonly Regex RxD = new Regex(@"(?<![A-Za-z0-9_])D[ \t]*:[ \t]*([^\s,;]+)", RegexOptions.IgnoreCase);

        private static string Hash(string id)
        {
            using (var sha = SHA1.Create())
                return BitConverter.ToString(sha.ComputeHash(Encoding.UTF8.GetBytes(id))).Replace("-", "").Substring(0, 16).ToLowerInvariant();
        }

        private static bool SenderAllowed(Settings s, MailData m)
        {
            if (string.IsNullOrWhiteSpace(s.AllowedSenders)) return true;
            foreach (string a in s.AllowedSenders.Split(new[] { ',', ';', ' ' }, StringSplitOptions.RemoveEmptyEntries))
                if (string.Equals(a.Trim(), m.FromEmail, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static void Finish(string hash, Row row, Action<Row> onRow)
        {
            Store.Add(hash, row);
            if (onRow != null) onRow(row);
        }

        /// <summary>
        /// status:search - only for mail that is ENCRYPTED (only I can read it) and SIGNED by a trusted sender.
        /// The answer is signed with my key and encrypted for the sender's public key, then sent back.
        /// </summary>
        private static void HandleSearch(IDbConnection conn, EwsClient ews, Settings s, MailData m, SmimeResult res,
                                         X509Certificate2 me, string a, string d, Row row)
        {
            if (res == null || !res.Encrypted)
            { row.Status = "IGNORED"; row.Info = "search must be encrypted (S/MIME) - only the owner may read it"; return; }
            if (!res.Signed || !res.SigValid)
            { row.Status = "IGNORED"; row.Info = "search must carry a valid digital signature"; return; }
            X509Certificate2 trusted = Smime.FindTrusted(res.Signer);
            if (trusted == null)
            { row.Status = "IGNORED"; row.Info = "signer certificate is not in the trusted folder (or expired)"; return; }

            int max = Math.Max(1, s.SearchMaxRows);
            bool more;
            List<string[]> found = OracleDb.Search(conn, s, a, d, max, out more);

            string crit = (a != null ? s.ColName1 + " like %" + a + "%" : "") +
                          (a != null && d != null ? " AND " : "") +
                          (d != null ? s.ColName2 + " like %" + d + "%" : "");
            var sb = new StringBuilder();
            sb.AppendLine("Search: " + crit);
            sb.AppendLine("Rows: " + found.Count + (more ? "  (more rows exist - limited to " + max + ")" : ""));
            sb.AppendLine();
            sb.AppendLine(string.Join(" | ", OracleDb.SearchHeader(s)));
            foreach (string[] line in found) sb.AppendLine(string.Join(" | ", line));

            string to = trusted.GetNameInfo(X509NameType.EmailName, false);
            if (string.IsNullOrEmpty(to)) to = m.FromEmail;

            ews.SendMime(Smime.BuildReply(to, "RE: " + m.Subject, sb.ToString(), me, trusted));
            row.Status = "SEARCHED";
            row.Info = found.Count + " row(s) sent (encrypted) to " + to;
        }

        /// <summary>
        /// Looks at new mail in the Inbox. For each mail with  A:xxx  and/or  D:yyy  it inserts one row into Oracle.
        /// Returns the number of rows inserted. Throws if the mail server or Oracle cannot be reached
        /// (nothing is lost - the same mails are tried again on the next check).
        /// </summary>
        public static int Run(Settings s, Action<Row> onRow)
        {
            if (s.StartFrom == default(DateTime)) s.StartFrom = DateTime.Now;

            var ews = new EwsClient(s);

            // 1) find mails that are new (newest first, stops as soon as a page has nothing new)
            var cands = new List<ItemRef>();
            int offset = 0;
            while (true)
            {
                bool last;
                List<ItemRef> page = ews.FindPage("inbox", offset, 100, out last);
                if (page.Count == 0) break;
                int fresh = 0;
                foreach (ItemRef it in page)
                    if (it.Received >= s.StartFrom && !Store.Has(Hash(it.Id))) { cands.Add(it); fresh++; }
                if (last || fresh == 0) break;
                offset += page.Count;
            }
            if (cands.Count == 0) return 0;
            cands.Reverse();                      // oldest first = same order as they arrived

            // 2) process them
            int inserted = 0;
            X509Certificate2 me = null;
            bool meLoaded = false;
            using (IDbConnection conn = OracleDb.Open(s.OracleConn))      // throws if Oracle is down -> retry next time
            {
                foreach (ItemRef it in cands)
                {
                    string h = Hash(it.Id);
                    MailData m = ews.GetMail(it.Id);
                    var row = new Row { Time = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") };

                    if (m == null) { row.Status = "IGNORED"; row.Info = "could not read mail"; Finish(h, row, onRow); continue; }
                    row.From = m.FromEmail;

                    if (!SenderAllowed(s, m)) { row.Status = "IGNORED"; row.Info = "sender not in allowed list"; Finish(h, row, onRow); continue; }

                    // S/MIME (encrypted and/or signed) mails: decrypt with MY private key, verify the signature
                    SmimeResult res = null;
                    if (Smime.IsSmime(m.Mime))
                    {
                        if (!meLoaded) { me = Smime.FindMyCert(s.CertThumbprint); meLoaded = true; }
                        res = Smime.Open(m.Mime, me);
                        if (res.Error != null)
                        {
                            row.Status = "IGNORED"; row.Info = "S/MIME problem: " + res.Error;
                            Finish(h, row, onRow); continue;
                        }
                    }
                    string text = m.Subject + "\n" + (res != null ? res.Text : m.Body);
                    Match ma = RxA.Match(text), md = RxD.Match(text);
                    string a = ma.Success ? ma.Groups[1].Value : null;
                    string d = md.Success ? md.Groups[1].Value : null;
                    row.A = a ?? ""; row.D = d ?? "";

                    if (a == null && d == null) { row.Status = "IGNORED"; row.Info = "no A: or D: value in mail"; Finish(h, row, onRow); continue; }

                    Match ms = RxS.Match(text);
                    string status = ms.Success ? ms.Groups[1].Value.ToLowerInvariant() : null;
                    if (status == null) { row.Status = "IGNORED"; row.Info = "no status:add / status:delete / status:search in mail"; Finish(h, row, onRow); continue; }

                    string who = string.IsNullOrWhiteSpace(s.FixedUser) ? m.FromEmail : s.FixedUser.Trim();
                    try
                    {
                        if (status == "add")
                        {
                            OracleDb.Add(conn, s, a, d, who);
                            row.Status = "ADDED"; row.Info = "inserted";
                            inserted++;
                        }
                        else if (status == "delete")
                        {
                            int rows = OracleDb.Delete(conn, s, a, d, who);
                            if (rows > 0) { row.Status = "DELETED"; row.Info = "marked deleted (" + rows + " row)"; inserted++; }
                            else { row.Status = "NOTFOUND"; row.Info = "no active row with this A/D pair - nothing deleted"; }
                        }
                        else
                        {
                            HandleSearch(conn, ews, s, m, res, me, a, d, row);
                        }
                        Finish(h, row, onRow);
                    }
                    catch (Exception ex)
                    {
                        if (conn.State != ConnectionState.Open) throw;        // connection problem: stop, retry later, count nothing

                        int n;
                        Attempts.TryGetValue(h, out n);
                        Attempts[h] = ++n;
                        Log.Write("Insert failed (" + n + "/" + MaxAttempts + ") for mail from " + m.FromEmail + ": " + ex.Message);
                        if (n >= MaxAttempts)                                  // bad data (too long, wrong type...) - do not block the queue
                        {
                            row.Status = "FAILED"; row.Info = ex.Message;
                            Finish(h, row, onRow);
                        }
                    }
                }
            }
            return inserted;
        }
    }
}
