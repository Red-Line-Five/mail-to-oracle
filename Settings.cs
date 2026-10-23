using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Xml.Serialization;

namespace OwaToOracle
{
    public class Settings
    {
        // ---- mail server (OWA / Exchange) ----
        public string Server = "";        // 192.168.1.1  (or the whole OWA link)
        public string User = "";
        public string Domain = "";
        public string PasswordEnc = "";   // DPAPI encrypted
        public bool TrustCert = true;

        // ---- Oracle ----
        public string OracleEnc = "";     // DPAPI encrypted connection string
        public string Table = "MY_TABLE";
        public string ColName1 = "NAME1";     // gets the A: value
        public string ColName2 = "NAME2";     // gets the D: value
        public string ColUser = "ADD_USER";
        public string ColDate = "ADD_DATE";
        public string ColDelUser = "DELETE_USER";   // filled when the same A:/D: pair is sent again
        public string ColDelDate = "DELETE_DATE";
        public string FixedUser = "";         // empty = use the sender's e-mail as add_user

        // ---- behaviour ----
        public string AllowedSenders = "";    // empty = accept mail from anyone; else comma list of e-mail addresses
        public int IntervalSeconds = 30;
        public string CertThumbprint = "";    // MY certificate (private key) used to decrypt mails and sign replies
        public int SearchMaxRows = 200;       // max rows in a status:search reply
        public bool AutoStart = true;
        public bool KeepAwake = true;
        public DateTime StartFrom;            // mail older than this is never processed

        [XmlIgnore]
        public string Password { get { return Dec(PasswordEnc); } set { PasswordEnc = Enc(value); } }

        [XmlIgnore]
        public string OracleConn { get { return Dec(OracleEnc); } set { OracleEnc = Enc(value); } }

        private static string Enc(string s)
        {
            return Convert.ToBase64String(
                ProtectedData.Protect(Encoding.UTF8.GetBytes(s ?? ""), null, DataProtectionScope.CurrentUser));
        }

        private static string Dec(string s)
        {
            try
            {
                if (string.IsNullOrEmpty(s)) return "";
                return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(s), null, DataProtectionScope.CurrentUser));
            }
            catch { return ""; }
        }

        public bool IsConfigured()
        {
            return !string.IsNullOrWhiteSpace(Server) && !string.IsNullOrWhiteSpace(User) &&
                   !string.IsNullOrWhiteSpace(OracleConn) && !string.IsNullOrWhiteSpace(Table);
        }

        public static string Dir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "OwaToOracle"); }
        }

        private static string FilePath { get { return Path.Combine(Dir, "settings.xml"); } }

        public static Settings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                    using (var fs = File.OpenRead(FilePath))
                        return (Settings)new XmlSerializer(typeof(Settings)).Deserialize(fs);
            }
            catch { }
            return new Settings();
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Dir);
                using (var fs = File.Create(FilePath))
                    new XmlSerializer(typeof(Settings)).Serialize(fs, this);
            }
            catch { }
        }
    }

    public static class Log
    {
        private static readonly object L = new object();
        public static string FilePath { get { return Path.Combine(Settings.Dir, "log.txt"); } }

        public static void Write(string msg)
        {
            try
            {
                lock (L)
                {
                    Directory.CreateDirectory(Settings.Dir);
                    File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + "  " + msg + Environment.NewLine);
                }
            }
            catch { }
        }
    }
}
