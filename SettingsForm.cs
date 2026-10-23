using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography.X509Certificates;
using System.Windows.Forms;
using System.Threading.Tasks;

namespace OwaToOracle
{
    public class SettingsForm : Form
    {
        private readonly Settings _s;
        private readonly TextBox txtServer = new TextBox(), txtUser = new TextBox(), txtDomain = new TextBox(),
            txtPass = new TextBox { UseSystemPasswordChar = true },
            txtOra = new TextBox { UseSystemPasswordChar = true },
            txtTable = new TextBox(), txtC1 = new TextBox(), txtC2 = new TextBox(), txtCU = new TextBox(), txtCD = new TextBox(), txtDU = new TextBox(), txtDD = new TextBox(),
            txtFixed = new TextBox(), txtAllowed = new TextBox();
        private readonly CheckBox chkTrust = new CheckBox { Text = "Trust server certificate (IP address / self-signed)" },
            chkShow = new CheckBox { Text = "Show Oracle connection string" },
            chkStart = new CheckBox { Text = "Start with Windows (hidden in tray)" },
            chkAwake = new CheckBox { Text = "Keep PC awake (24/7)" };
        private readonly NumericUpDown numSec = new NumericUpDown { Minimum = 5, Maximum = 86400 };
        private readonly NumericUpDown numRows = new NumericUpDown { Minimum = 1, Maximum = 5000, Value = 200 };
        private readonly ComboBox cmbCert = new ComboBox { DropDownStyle = ComboBoxStyle.DropDownList };

        private class CertItem
        {
            public string Thumb = "", Text = "";
            public override string ToString() { return Text; }
        }
        private readonly TableLayoutPanel t = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, Padding = new Padding(12), AutoScroll = true };

        public SettingsForm(Settings s)
        {
            _s = s;
            Text = "Settings";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            StartPosition = FormStartPosition.CenterParent;
            Width = 680; Height = 860;

            t.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            Head("MAIL (OWA / Exchange)");
            AddRow("Server (OWA address):", txtServer);
            AddRow("Username:", txtUser);
            AddRow("Domain (optional):", txtDomain);
            AddRow("Password:", txtPass);
            Wide(chkTrust);
            Head("ORACLE");
            AddRow("Connection string:", txtOra);
            Wide(chkShow);
            AddRow("Table:", txtTable);
            AddRow("Column for  A:  value:", txtC1);
            AddRow("Column for  D:  value:", txtC2);
            AddRow("Column add_user:", txtCU);
            AddRow("Column add_date:", txtCD);
            AddRow("Column delete_user:", txtDU);
            AddRow("Column delete_date:", txtDD);
            AddRow("add_user fixed text:", txtFixed);
            Head("BEHAVIOUR");
            AddRow("Only mail from (optional):", txtAllowed);
            AddRow("Check mail every (sec):", numSec);
            Wide(chkStart);
            Wide(chkAwake);
            Head("ENCRYPTION (S/MIME) - for status:search");
            AddRow("My certificate:", cmbCert);
            AddRow("Max rows in reply:", numRows);
            var certBtns = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
            var btnExport = new Button { Text = "Export my public certificate...", AutoSize = true };
            var btnImport = new Button { Text = "Add trusted sender (.cer)...", AutoSize = true };
            var btnTrusted = new Button { Text = "Open trusted folder", AutoSize = true };
            certBtns.Controls.AddRange(new Control[] { btnExport, btnImport, btnTrusted });
            Wide(certBtns);

            var btns = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, FlowDirection = FlowDirection.RightToLeft, Padding = new Padding(8) };
            var ok = new Button { Text = "Save", DialogResult = DialogResult.OK, Width = 80 };
            var cancel = new Button { Text = "Cancel", DialogResult = DialogResult.Cancel, Width = 80 };
            var testMail = new Button { Text = "Test mail login", Width = 110 };
            var testOra = new Button { Text = "Test Oracle", Width = 100 };
            btns.Controls.AddRange(new Control[] { ok, cancel, testMail, testOra });
            Controls.Add(t);
            Controls.Add(btns);
            AcceptButton = ok; CancelButton = cancel;

            txtServer.Text = s.Server; txtUser.Text = s.User; txtDomain.Text = s.Domain; chkTrust.Checked = s.TrustCert;
            txtOra.Text = s.OracleConn;
            txtTable.Text = s.Table; txtC1.Text = s.ColName1; txtC2.Text = s.ColName2; txtCU.Text = s.ColUser; txtCD.Text = s.ColDate; txtDU.Text = s.ColDelUser; txtDD.Text = s.ColDelDate;
            txtFixed.Text = s.FixedUser; txtAllowed.Text = s.AllowedSenders;
            numSec.Value = Math.Max(5, s.IntervalSeconds);
            chkStart.Checked = s.AutoStart; chkAwake.Checked = s.KeepAwake;
            txtPass.Text = string.IsNullOrEmpty(s.PasswordEnc) ? "" : "********";   // real password stays encrypted
            txtPass.Tag = "keep";

            numRows.Value = Math.Max(1, Math.Min(5000, s.SearchMaxRows));
            LoadCerts(s.CertThumbprint);

            btnExport.Click += (a, b) =>
            {
                try
                {
                    var item = cmbCert.SelectedItem as CertItem;
                    X509Certificate2 c = item == null ? null : Smime.FindMyCert(item.Thumb);
                    if (c == null) { MessageBox.Show("Select your certificate first."); return; }
                    using (var dlg = new SaveFileDialog { Filter = "Certificate (*.cer)|*.cer", FileName = "my-public-certificate.cer" })
                    {
                        if (dlg.ShowDialog() != DialogResult.OK) return;
                        File.WriteAllBytes(dlg.FileName, c.Export(X509ContentType.Cert));   // public part only - safe to send to senders
                        MessageBox.Show("Saved. Give this file to the people who send you encrypted search mails.", "Certificate");
                    }
                }
                catch (Exception ex) { MessageBox.Show(ex.Message); }
            };

            btnImport.Click += (a, b) =>
            {
                using (var dlg = new OpenFileDialog { Filter = "Certificate (*.cer;*.crt;*.pem)|*.cer;*.crt;*.pem" })
                {
                    if (dlg.ShowDialog() != DialogResult.OK) return;
                    try
                    {
                        var c = new X509Certificate2(dlg.FileName);
                        Directory.CreateDirectory(Smime.TrustedDir);
                        File.WriteAllBytes(Path.Combine(Smime.TrustedDir, c.Thumbprint + ".cer"), c.Export(X509ContentType.Cert));
                        MessageBox.Show("Trusted sender added:\n" + c.Subject + "\nvalid until " + c.NotAfter.ToString("yyyy-MM-dd"), "Certificate");
                    }
                    catch (Exception ex) { MessageBox.Show(ex.Message, "Not a certificate"); }
                }
            };

            btnTrusted.Click += (a, b) =>
            {
                Directory.CreateDirectory(Smime.TrustedDir);
                Process.Start("explorer.exe", "\"" + Smime.TrustedDir + "\"");
            };

            chkShow.CheckedChanged += (a, b) => txtOra.UseSystemPasswordChar = !chkShow.Checked;

            testMail.Click += async (a, b) =>
            {
                testMail.Enabled = false;
                try
                {
                    Settings tmp = Collect(new Settings());
                    await Task.Run(() => { bool last; new EwsClient(tmp).FindPage("inbox", 0, 1, out last); });
                    MessageBox.Show("Mail login OK - the inbox is readable.", "Test");
                }
                catch (Exception ex) { MessageBox.Show(ex.Message, "Mail test failed"); }
                finally { testMail.Enabled = true; }
            };

            testOra.Click += async (a, b) =>
            {
                testOra.Enabled = false;
                try
                {
                    Settings tmp = Collect(new Settings());
                    await Task.Run(() => OracleDb.Check(tmp));
                    MessageBox.Show("Oracle OK - connected, table and columns exist.", "Test");
                }
                catch (Exception ex) { MessageBox.Show(ex.Message, "Oracle test failed"); }
                finally { testOra.Enabled = true; }
            };

            FormClosing += (a, e) =>
            {
                if (DialogResult != DialogResult.OK) return;
                Collect(_s);
                if (_s.StartFrom == default(DateTime)) _s.StartFrom = DateTime.Now;   // only mail from now on
            };
        }

        private void LoadCerts(string selected)
        {
            cmbCert.Items.Clear();
            cmbCert.Items.Add(new CertItem { Thumb = "", Text = "(none - encrypted mail / status:search disabled)" });
            try
            {
                using (var st = new X509Store("My", StoreLocation.CurrentUser))
                {
                    st.Open(OpenFlags.ReadOnly);
                    foreach (X509Certificate2 c in st.Certificates)
                    {
                        if (!c.HasPrivateKey || c.NotAfter < DateTime.Now) continue;
                        cmbCert.Items.Add(new CertItem
                        {
                            Thumb = c.Thumbprint,
                            Text = c.GetNameInfo(X509NameType.SimpleName, false) + "  [" + c.Thumbprint.Substring(0, 8) + "]  until " + c.NotAfter.ToString("yyyy-MM-dd")
                        });
                    }
                }
            }
            catch { }
            int idx = 0;
            for (int i = 0; i < cmbCert.Items.Count; i++)
                if (string.Equals(((CertItem)cmbCert.Items[i]).Thumb, selected ?? "", StringComparison.OrdinalIgnoreCase)) idx = i;
            cmbCert.SelectedIndex = idx;
        }

        private void Head(string text)
        {
            var l = new Label { Text = text, AutoSize = true, Font = new System.Drawing.Font(Font, System.Drawing.FontStyle.Bold), Margin = new Padding(0, 12, 0, 2) };
            t.Controls.Add(l);
            t.SetColumnSpan(l, 2);
        }

        private void AddRow(string label, Control c)
        {
            t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Margin = new Padding(0, 6, 0, 6) });
            c.Dock = DockStyle.Fill;
            t.Controls.Add(c);
        }

        private void Wide(Control c)
        {
            c.AutoSize = true;
            t.Controls.Add(c);
            t.SetColumnSpan(c, 2);
        }

        private Settings Collect(Settings s)
        {
            s.Server = txtServer.Text.Trim(); s.User = txtUser.Text.Trim(); s.Domain = txtDomain.Text.Trim();
            s.TrustCert = chkTrust.Checked;
            if (txtPass.Text != "********") s.Password = txtPass.Text; else if (!ReferenceEquals(s, _s)) s.Password = _s.Password;
            s.OracleConn = txtOra.Text.Trim();
            s.Table = txtTable.Text.Trim(); s.ColName1 = txtC1.Text.Trim(); s.ColName2 = txtC2.Text.Trim();
            s.ColUser = txtCU.Text.Trim(); s.ColDate = txtCD.Text.Trim();
            s.ColDelUser = txtDU.Text.Trim(); s.ColDelDate = txtDD.Text.Trim();
            s.FixedUser = txtFixed.Text.Trim(); s.AllowedSenders = txtAllowed.Text.Trim();
            s.IntervalSeconds = (int)numSec.Value;
            s.AutoStart = chkStart.Checked; s.KeepAwake = chkAwake.Checked;
            var ci = cmbCert.SelectedItem as CertItem;
            s.CertThumbprint = ci == null ? "" : ci.Thumb;
            s.SearchMaxRows = (int)numRows.Value;
            return s;
        }
    }
}
