using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Security;
using System.Text;
using System.Xml.Linq;

namespace OwaToOracle
{
    public class ItemRef { public string Id; public DateTime Received; }

    public class MailData
    {
        public string Subject = "", FromName = "", FromEmail = "", Body = "";
        public DateTime Received;
        public byte[] Mime;          // raw mail (needed for S/MIME)
    }

    /// <summary>Exchange Web Services over plain HTTPS + XML. Built-in .NET classes only.</summary>
    public class EwsClient
    {
        static readonly XNamespace M = "http://schemas.microsoft.com/exchange/services/2006/messages";
        static readonly XNamespace T = "http://schemas.microsoft.com/exchange/services/2006/types";

        private readonly string _url;
        private readonly CredentialCache _cache = new CredentialCache();

        public EwsClient(Settings s)
        {
            _url = EwsUrl(s.Server);

            string user = s.User.Trim(), domain = s.Domain.Trim();
            int bs = user.IndexOf('\\');
            if (bs > 0) { domain = user.Substring(0, bs); user = user.Substring(bs + 1); }

            var cred = string.IsNullOrEmpty(domain)
                ? new NetworkCredential(user, s.Password)
                : new NetworkCredential(user, s.Password, domain);

            var uri = new Uri(_url);
            _cache.Add(uri, "Basic", cred);
            _cache.Add(uri, "NTLM", cred);
            _cache.Add(uri, "Negotiate", cred);

            ServicePointManager.ServerCertificateValidationCallback = s.TrustCert
                ? (System.Net.Security.RemoteCertificateValidationCallback)((a, b, c, d) => true)
                : null;
        }

        public static string EwsUrl(string server)
        {
            server = (server ?? "").Trim();
            if (server.IndexOf("://", StringComparison.Ordinal) < 0) server = "https://" + server;
            return new Uri(server).GetLeftPart(UriPartial.Authority) + "/EWS/Exchange.asmx";
        }

        private static string Envelope(string body)
        {
            return "<?xml version=\"1.0\" encoding=\"utf-8\"?>" +
                   "<soap:Envelope xmlns:soap=\"http://schemas.xmlsoap.org/soap/envelope/\" " +
                   "xmlns:t=\"http://schemas.microsoft.com/exchange/services/2006/types\" " +
                   "xmlns:m=\"http://schemas.microsoft.com/exchange/services/2006/messages\">" +
                   "<soap:Header><t:RequestServerVersion Version=\"Exchange2010_SP2\"/></soap:Header>" +
                   "<soap:Body>" + body + "</soap:Body></soap:Envelope>";
        }

        private XDocument Post(string body)
        {
            var req = (HttpWebRequest)WebRequest.Create(_url);
            req.Method = "POST";
            req.ContentType = "text/xml; charset=utf-8";
            req.Credentials = _cache;
            req.AllowAutoRedirect = false;
            req.Timeout = 120000;
            req.ReadWriteTimeout = 120000;
            req.UserAgent = "OwaToOracle";
            req.ConnectionGroupName = "ews";
            req.UnsafeAuthenticatedConnectionSharing = true;

            byte[] data = Encoding.UTF8.GetBytes(Envelope(body));
            req.ContentLength = data.Length;

            string xml = null;
            HttpStatusCode code = 0;
            try
            {
                using (var rs = req.GetRequestStream()) rs.Write(data, 0, data.Length);
                using (var resp = (HttpWebResponse)req.GetResponse())
                using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8))
                {
                    code = resp.StatusCode;
                    xml = sr.ReadToEnd();
                }
            }
            catch (WebException ex)
            {
                var hr = ex.Response as HttpWebResponse;
                if (hr == null) throw new Exception("Cannot reach the mail server: " + ex.Message);
                code = hr.StatusCode;
                if (code == HttpStatusCode.Unauthorized)
                    throw new Exception("Mail login failed (401). Check username, password and domain.");
                if (code == HttpStatusCode.NotFound)
                    throw new Exception("EWS not found (404). Exchange Web Services may be disabled.");
                using (var sr = new StreamReader(hr.GetResponseStream(), Encoding.UTF8)) xml = sr.ReadToEnd();
            }

            int c = (int)code;
            if (c >= 300 && c < 400)
                throw new Exception("Server redirected the request (HTTP " + c + "). EWS is probably disabled or blocked.");

            XDocument doc;
            try { doc = XDocument.Parse(xml); }
            catch { throw new Exception("Unexpected reply (HTTP " + c + "). EWS may be disabled or blocked."); }

            var fault = doc.Descendants("faultstring").FirstOrDefault();
            if (fault != null) throw new Exception("Server error: " + fault.Value);

            foreach (var rm in doc.Descendants())
            {
                var rc = rm.Attribute("ResponseClass");
                if (rc != null && rc.Value == "Error")
                {
                    var txt = rm.Element(M + "MessageText");
                    throw new Exception(txt != null ? txt.Value : "Exchange returned an error.");
                }
            }
            return doc;
        }

        private static DateTime ParseDate(string s)
        {
            DateTime d;
            if (DateTime.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out d))
                return d.ToLocalTime();
            return DateTime.Now;
        }

        /// <summary>One page (newest first) of "inbox".</summary>
        public List<ItemRef> FindPage(string folder, int offset, int max, out bool last)
        {
            string body =
                "<m:FindItem Traversal=\"Shallow\">" +
                "<m:ItemShape><t:BaseShape>IdOnly</t:BaseShape><t:AdditionalProperties>" +
                "<t:FieldURI FieldURI=\"item:DateTimeReceived\"/></t:AdditionalProperties></m:ItemShape>" +
                "<m:IndexedPageItemView MaxEntriesReturned=\"" + max + "\" Offset=\"" + offset + "\" BasePoint=\"Beginning\"/>" +
                "<m:SortOrder><t:FieldOrder Order=\"Descending\"><t:FieldURI FieldURI=\"item:DateTimeReceived\"/></t:FieldOrder></m:SortOrder>" +
                "<m:ParentFolderIds><t:DistinguishedFolderId Id=\"" + folder + "\"/></m:ParentFolderIds>" +
                "</m:FindItem>";

            var doc = Post(body);
            var list = new List<ItemRef>();
            last = true;
            var root = doc.Descendants(M + "RootFolder").FirstOrDefault();
            if (root == null) return list;

            var a = root.Attribute("IncludesLastItemInRange");
            last = a == null || a.Value != "false";

            var items = root.Element(T + "Items");
            if (items == null) return list;
            foreach (var e in items.Elements())
            {
                var idEl = e.Element(T + "ItemId");
                if (idEl == null) continue;
                list.Add(new ItemRef
                {
                    Id = (string)idEl.Attribute("Id"),
                    Received = ParseDate((string)e.Element(T + "DateTimeReceived"))
                });
            }
            return list;
        }

        /// <summary>Reads one message: subject, sender and the plain-text body.</summary>
        public MailData GetMail(string id)
        {
            string body =
                "<m:GetItem><m:ItemShape><t:BaseShape>IdOnly</t:BaseShape><t:IncludeMimeContent>true</t:IncludeMimeContent><t:BodyType>Text</t:BodyType>" +
                "<t:AdditionalProperties>" +
                "<t:FieldURI FieldURI=\"item:Subject\"/><t:FieldURI FieldURI=\"item:Body\"/>" +
                "<t:FieldURI FieldURI=\"message:From\"/><t:FieldURI FieldURI=\"item:DateTimeReceived\"/>" +
                "</t:AdditionalProperties></m:ItemShape>" +
                "<m:ItemIds><t:ItemId Id=\"" + SecurityElement.Escape(id) + "\"/></m:ItemIds></m:GetItem>";

            var doc = Post(body);
            var rm = doc.Descendants(M + "GetItemResponseMessage").FirstOrDefault();
            if (rm == null) return null;
            var items = rm.Element(M + "Items");
            if (items == null) return null;
            var item = items.Elements().FirstOrDefault();
            if (item == null) return null;

            var mail = new MailData();
            mail.Subject = (string)item.Element(T + "Subject") ?? "";
            mail.Body = (string)item.Element(T + "Body") ?? "";
            mail.Received = ParseDate((string)item.Element(T + "DateTimeReceived"));

            string b64 = (string)item.Element(T + "MimeContent");
            if (!string.IsNullOrEmpty(b64)) mail.Mime = Convert.FromBase64String(b64);

            var from = item.Element(T + "From");
            if (from != null)
            {
                var mb = from.Element(T + "Mailbox");
                if (mb != null)
                {
                    mail.FromName = (string)mb.Element(T + "Name") ?? "";
                    string email = (string)mb.Element(T + "EmailAddress") ?? "";
                    string routing = (string)mb.Element(T + "RoutingType") ?? "";
                    // internal senders can come back as a long Exchange legacy address ("EX") - use their name then
                    mail.FromEmail = (routing == "EX" || email.Length == 0) ? mail.FromName : email;
                }
            }
            return mail;
        }

        /// <summary>Sends a complete MIME mail (recipients are taken from its To: header) and keeps a copy in Sent Items.</summary>
        public void SendMime(byte[] mime)
        {
            string body =
                "<m:CreateItem MessageDisposition=\"SendAndSaveCopy\">" +
                "<m:SavedItemFolderId><t:DistinguishedFolderId Id=\"sentitems\"/></m:SavedItemFolderId>" +
                "<m:Items><t:Message><t:MimeContent CharacterSet=\"UTF-8\">" + Convert.ToBase64String(mime) +
                "</t:MimeContent></t:Message></m:Items></m:CreateItem>";
            Post(body);
        }
    }
}
