# OwaToOracle - mail -> Oracle, runs 24/7 (WinForms, .NET Framework 4.6.1)

The mail says what to do with  status:add  or  status:delete

    A:fff D:eee status:add        ->  INSERT a new row
    A:fff D:eee status:delete     ->  UPDATE the active row with this A/D pair

**status:add**  ->  new row:

| name1 | name2 | add_user        | add_date | delete_user | delete_date |
|-------|-------|-----------------|----------|-------------|-------------|
| fff   | eee   | sender's e-mail | SYSDATE  |             |             |

**status:delete**  ->

    UPDATE table SET delete_user = <sender>, delete_date = SYSDATE
    WHERE name1 = 'fff' AND name2 = 'eee' AND delete_date IS NULL

If no active row matches, nothing is changed and the list shows NOTFOUND.
A mail without status:add / status:delete (or without A:/D:) is IGNORED.
The three parts can be in the subject or in the body, on one line or on separate lines.

* A: value -> column name1      D: value -> column name2  (compared exactly, upper/lower case matters)
* Only A: or only D: in the mail -> the other column is NULL (and matched as IS NULL on delete).
* Column / table names are set in Settings (create_table.sql has an example table with the
  delete_user / delete_date columns - if your table lacks them: ALTER TABLE ... ADD ...).
* add_user / delete_user = sender's e-mail, or a fixed text if you fill "add_user fixed text".

## 1. Build
Open OwaToOracle.sln in Visual Studio (needs the .NET Framework 4.6.1 targeting pack) and build.
No NuGet, no MailKit/MimeKit/EWS DLL. Mail is read with plain HTTPS+XML (EWS) - same login as OWA.

## 2. Oracle driver (one file, needed at run time only)
Oracle always needs its own driver. Get  Oracle.ManagedDataAccess.dll  (version 19.x, folder lib\net40
of the NuGet package "Oracle.ManagedDataAccess", or from an ODP.NET install) and put it
NEXT TO OwaToOracle.exe  (or into the project folder before building - it is copied automatically).
It is loaded at run time; the project does not reference it. No Oracle client install needed.

Connection string example (EZ connect):
    User Id=myuser;Password=mypass;Data Source=192.168.1.20:1521/ORCL;

## 3. First start
Settings -> mail server/user/password -> "Test mail login"; Oracle string/table -> "Test Oracle"; Save.
Only mail that arrives AFTER you first save is processed (old inbox mail is never inserted).

## 24/7 behaviour
* Checks the inbox every N seconds (default 30). Tray icon; X hides, Exit only from tray menu.
* Starts with Windows (hidden), keeps the PC awake, one instance only, restarts itself after a crash.
* Every mail is handled once (processed.tsv). If mail server or Oracle is down, nothing is lost -
  the same mails are tried again on the next check. A mail whose data Oracle rejects 3 times
  (too long, ...) is marked FAILED so it cannot block the others.
* Files: %AppData%\OwaToOracle\  settings.xml (passwords encrypted with Windows DPAPI), processed.tsv, log.txt

## status:search  (encrypted)

    A:dd status:search            -> rows where name1 LIKE %dd%
    D:ss status:search            -> rows where name2 LIKE %ss%
    A:dd D:ss status:search       -> name1 LIKE %dd% AND name2 LIKE %ss%

The answer is mailed back to the sender (max rows in Settings). Search is case-insensitive.
Put the values in the BODY (the subject is never encrypted).

A search mail is only answered if it is  1) ENCRYPTED to my certificate  and  2) SIGNED by a trusted sender.
Otherwise it is IGNORED and nothing is read from Oracle. (Anyone can get my public key, so encryption alone
does not prove who is asking - the signature does.)  The reply is signed with my key and encrypted for the sender.

### One-time setup - ME (the owner)  (Windows 10/11, PowerShell)
    $c = New-SelfSignedCertificate -Subject "CN=Me, E=me@company.com" -KeyUsage DigitalSignature,KeyEncipherment `
         -KeySpec KeyExchange -KeyExportPolicy Exportable -Type Custom -KeyLength 2048 -HashAlgorithm SHA256 `
         -TextExtension @("2.5.29.37={text}1.3.6.1.5.5.7.3.4","2.5.29.17={text}email=me@company.com") `
         -NotAfter (Get-Date).AddYears(5) -CertStoreLocation Cert:\CurrentUser\My
Then in the app: Settings > My certificate (pick it) > "Export my public certificate..." -> give the .cer to the sender.
(Never give anyone the private key / .pfx.)

### One-time setup - the SENDER (Outlook)
1. Has his own S/MIME certificate (same PowerShell command with his e-mail) and sends his public .cer to me.
2. Imports MY .cer into his Outlook CONTACT for my address: open the contact > Certificates tab > Import.
   (That is how Outlook knows my public key.)
3. Writes the mail, Options > Encrypt  AND  Sign, and sends it.
4. Me: Settings > "Add trusted sender (.cer)..." and pick his .cer.
