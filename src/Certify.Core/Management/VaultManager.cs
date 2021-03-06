using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using ACMESharp;
using ACMESharp.POSH;
using ACMESharp.POSH.Util;
using ACMESharp.Util;
using ACMESharp.Vault.Model;
using ACMESharp.Vault.Profile;
using ACMESharp.Vault.Providers;
using ACMESharp.WebServer;
using Certify.Models;

namespace Certify
{
    public class VaultManager
    {
        private VaultInfo vaultConfig;
        private PowershellManager powershellManager;
        private string vaultFolderPath;
        private string vaultFilename;
        public List<ActionLogItem> ActionLogs { get; }

        public string VaultFolderPath
        {
            get { return vaultFolderPath; }
        }

        public PowershellManager PowershellManager
        {
            get
            {
                return this.powershellManager;
            }
        }

        #region Vault

        public bool InitVault(bool staging = true)
        {
            string apiURI = InitializeVault.WELL_KNOWN_BASE_SERVICES[InitializeVault.WELL_KNOWN_LESTAGE];
            if (!staging)
            {
                //live api
                apiURI = InitializeVault.WELL_KNOWN_BASE_SERVICES[InitializeVault.WELL_KNOWN_LE];
            }
            powershellManager.InitializeVault(apiURI);

            this.vaultFolderPath = GetVaultPath();

            //create default manual http provider (challenge/response by placing answer in well known location on website for server to fetch);
            //powershellManager.NewProviderConfig("Manual", "manualHttpProvider");
            return true;
        }

        public VaultManager(string vaultFolderPath, string vaultFilename)
        {
            this.vaultFolderPath = vaultFolderPath;
            this.vaultFilename = vaultFilename;

            this.ActionLogs = new List<ActionLogItem>();

            powershellManager = new PowershellManager(vaultFolderPath, this.ActionLogs);
#if DEBUG
            this.InitVault(staging: true);
#else
            this.InitVault(staging: false);
#endif
            ReloadVaultConfig();

            //register default PKI provider
            //ACMESharp.PKI.CertificateProvider.RegisterProvider<ACMESharp.PKI.Providers.OpenSslLibProvider>();
            ACMESharp.PKI.CertificateProvider.RegisterProvider<ACMESharp.PKI.Providers.BouncyCastleProvider>();
        }

        private void LogAction(string command, string result = null)
        {
            if (this.ActionLogs != null)
            {
                this.ActionLogs.Add(new ActionLogItem { Command = command, Result = result, DateTime = DateTime.Now });
            }
        }

        public VaultInfo LoadVaultFromFile()
        {
            using (var vlt = ACMESharp.POSH.Util.VaultHelper.GetVault())
            {
                vlt.OpenStorage(true);
                var v = vlt.LoadVault();
                return v;
            }
        }

        public VaultInfo GetVaultConfig()
        {
            if (vaultConfig != null)
            {
                return vaultConfig;
            }
            else return null;
        }

        public void CleanupVault(Guid? identifierToRemove = null, bool includeDupeIdentifierRemoval = false)
        {
            //remove duplicate identifiers etc

            using (var vlt = ACMESharp.POSH.Util.VaultHelper.GetVault())
            {
                vlt.OpenStorage();
                var v = vlt.LoadVault();

                List<Guid> toBeRemoved = new List<Guid>();
                if (identifierToRemove != null)
                {
                    if (v.Identifiers.Keys.Any(i => i == (Guid)identifierToRemove))
                    {
                        toBeRemoved.Add((Guid)identifierToRemove);
                    }
                }
                else
                {
                    //find all orphaned identified
                    if (v.Identifiers != null)
                    {
                        foreach (var k in v.Identifiers.Keys)
                        {
                            var identifier = v.Identifiers[k];

                            var certs = v.Certificates.Values.Where(c => c.IdentifierRef == identifier.Id);
                            if (!certs.Any())
                            {
                                toBeRemoved.Add(identifier.Id);
                            }
                        }
                    }
                }

                foreach (var i in toBeRemoved)
                {
                    v.Identifiers.Remove(i);
                }
                //

                //find and remove certificates with no valid identifier in vault or with empty settings
                toBeRemoved = new List<Guid>();

                if (v.Certificates != null)
                {
                    foreach (var c in v.Certificates)
                    {
                        if (
                            String.IsNullOrEmpty(c.IssuerSerialNumber) //no valid issuer serial
                            ||
                            !v.Identifiers.ContainsKey(c.IdentifierRef) //no existing Identifier
                            )
                        {
                            toBeRemoved.Add(c.Id);
                        }
                    }

                    foreach (var i in toBeRemoved)
                    {
                        v.Certificates.Remove(i);
                    }
                }

                /*if (includeDupeIdentifierRemoval)
                {
                    //remove identifiers where the dns occurs more than once
                    foreach (var i in v.Identifiers)
                    {
                        var count = v.Identifiers.Values.Where(l => l.Dns == i.Dns).Count();
                        if (count > 1)
                        {
                            //identify most recent Identifier (based on assigned, non-expired cert), delete all the others

                            toBeRemoved.Add(i.Id);
                        }
                    }
                }*/

                vlt.SaveVault(v);
            }
        }

        public void ReloadVaultConfig()
        {
            this.vaultConfig = LoadVaultFromFile();
        }

        public bool IsValidVaultPath(string vaultPathFolder)
        {
            string vaultFile = vaultPathFolder + "\\" + LocalDiskVault.VAULT;
            if (File.Exists(vaultFile))
            {
                return true;
            }
            else
            {
                return false;
            }
        }

        public string GetVaultPath()
        {
            using (var vlt = (LocalDiskVault)ACMESharp.POSH.Util.VaultHelper.GetVault())
            {
                this.vaultFolderPath = vlt.RootPath;
            }
            return this.vaultFolderPath;
        }

        public bool HasContacts()
        {
            if (this.vaultConfig.Registrations != null && this.vaultConfig.Registrations.Count > 0)
            {
                return true;
            }
            else
            {
                return false;
            }
        }

        public IdentifierInfo GetIdentifier(string alias, bool reloadVaultConfig = false)
        {
            if (reloadVaultConfig)
            {
                ReloadVaultConfig();
            }

            var identifiers = GetIdentifiers();
            if (identifiers != null)
            {
                //find best match for given alias/id
                var result = identifiers.FirstOrDefault(i => i.Alias == alias);
                if (result == null)
                {
                    result = identifiers.FirstOrDefault(i => i.Dns == alias);
                }
                if (result == null)
                {
                    result = identifiers.FirstOrDefault(i => i.Id.ToString() == alias);
                }
                return result;
            }
            else
            {
                return null;
            }
        }

        public List<IdentifierInfo> GetIdentifiers()
        {
            if (vaultConfig != null && vaultConfig.Identifiers != null)
            {
                return vaultConfig.Identifiers.Values.ToList();
            }
            else return null;
        }

        public ProviderProfileInfo GetProviderConfig(string alias)
        {
            var vaultConfig = this.GetVaultConfig();
            if (vaultConfig.ProviderProfiles != null)
            {
                return vaultConfig.ProviderProfiles.Values.FirstOrDefault(p => p.Alias == alias);
            }
            else return null;
        }

        #endregion Vault

        #region Registration

        public void AddNewRegistration(string contacts)
        {
            powershellManager.NewRegistration(contacts);

            powershellManager.AcceptRegistrationTOS();
        }

        internal bool DeleteRegistrationInfo(Guid id)
        {
            using (var vlt = ACMESharp.POSH.Util.VaultHelper.GetVault())
            {
                try
                {
                    vlt.OpenStorage(true);
                    vaultConfig.Registrations.Remove(id);
                    vlt.SaveVault(vaultConfig);
                    return true;
                }
                catch (Exception e)
                {
                    // TODO: Logging of errors.
                    System.Windows.Forms.MessageBox.Show(e.Message, "Error", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
                    return false;
                }
            }
        }

        internal bool DeleteIdentifierByDNS(string dns)
        {
            using (var vlt = ACMESharp.POSH.Util.VaultHelper.GetVault())
            {
                try
                {
                    vlt.OpenStorage(true);
                    if (vaultConfig.Identifiers != null)
                    {
                        var idsToRemove = vaultConfig.Identifiers.Values.Where(i => i.Dns == dns);
                        List<Guid> removing = new List<Guid>();
                        foreach (var identifier in idsToRemove)
                        {
                            removing.Add(identifier.Id);
                        }
                        foreach (var identifier in removing)
                        {
                            vaultConfig.Identifiers.Remove(identifier);
                        }

                        vlt.SaveVault(vaultConfig);
                    }

                    return true;
                }
                catch (Exception e)
                {
                    // TODO: Logging of errors.
                    System.Windows.Forms.MessageBox.Show(e.Message, "Error", System.Windows.Forms.MessageBoxButtons.OK, System.Windows.Forms.MessageBoxIcon.Error);
                    return false;
                }
            }
        }

        public bool DeleteRegistrationInfo(string Id)
        {
            return false;
        }

        #endregion Registration

        #region Certificates

        public bool CertExists(string domainAlias)
        {
            var certRef = "cert_" + domainAlias;

            if (vaultConfig.Certificates != null && vaultConfig.Certificates.Values.Any(c => c.Alias == certRef))
            {
                return true;
            }
            else
            {
                return false;
            }
        }

        public void UpdateAndExportCertificate(string certAlias)
        {
            try
            {
                powershellManager.UpdateCertificate(certAlias);
                ReloadVaultConfig();

                var certInfo = GetCertificate(certAlias);

                // if we have our first cert files, lets export the pfx as well
                ExportCertificate(certAlias, pfxOnly: true);
            }
            catch (Exception exp)
            {
                System.Diagnostics.Debug.WriteLine(exp.ToString());
            }
        }

        public string CreateCertificate(string domainAlias)
        {
            var certRef = "cert_" + domainAlias;

            powershellManager.NewCertificate(domainAlias, certRef);

            ReloadVaultConfig();

            try
            {
                var apiResult = powershellManager.SubmitCertificate(certRef);

                //give LE time to generate cert before fetching fresh status info
                Thread.Sleep(1000);
            }
            catch (Exception exp)
            {
                System.Diagnostics.Debug.WriteLine(exp.ToString());
            }

            ReloadVaultConfig();

            UpdateAndExportCertificate(certRef);

            return certRef;
        }

        public string GetCertificateFilePath(Guid id, string assetTypeFolder = LocalDiskVault.CRTDR)
        {
            GetVaultPath();
            var cert = vaultConfig.Certificates[id];
            if (cert != null)
            {
                return this.VaultFolderPath + "\\" + assetTypeFolder;
            }
            return null;
        }

        public CertificateInfo GetCertificate(string reference, bool reloadVaultConfig = false)
        {
            if (reloadVaultConfig)
            {
                this.ReloadVaultConfig();
            }

            if (vaultConfig.Certificates != null)
            {
                var cert = vaultConfig.Certificates.Values.FirstOrDefault(c => c.Alias == reference);
                if (cert == null)
                {
                    cert = vaultConfig.Certificates.Values.FirstOrDefault(c => c.Id.ToString() == reference);
                }
                return cert;
            }
            return null;
        }

        public void ExportCertificate(string certRef, bool pfxOnly = false)
        {
            GetVaultPath();
            if (!Directory.Exists(VaultFolderPath + "\\" + LocalDiskVault.ASSET))
            {
                Directory.CreateDirectory(VaultFolderPath + "\\" + LocalDiskVault.ASSET);
            }
            powershellManager.ExportCertificate(certRef, this.VaultFolderPath, pfxOnly);
        }

        public PendingAuthorization BeginRegistrationAndValidation(CertRequestConfig requestConfig, string identifierAlias)
        {
            string domain = requestConfig.Domain;

            if (GetIdentifier(identifierAlias) == null)
            {
                //if an identifier exists for the same dns in vault, remove it to avoid confusion
                this.DeleteIdentifierByDNS(domain);
                var result = powershellManager.NewIdentifier(domain, identifierAlias, "Identifier:" + domain);
                if (!result.IsOK) return null;
            }

            var identifier = this.GetIdentifier(identifierAlias, reloadVaultConfig: true);

            /*
            //config file now has a temp path to write to, begin challenge (writes to temp file with challenge content)
            */
            if (identifier.Authorization.IsPending())
            {
                var ccrResult = powershellManager.CompleteChallenge(identifier.Alias, regenerate: true);

                if (ccrResult.IsOK)
                {
                    bool extensionlessConfigOK = false;
                    bool checkViaProxy = true;

                    //get challenge info
                    ReloadVaultConfig();
                    identifier = GetIdentifier(identifierAlias);
                    var challengeInfo = identifier.Challenges.FirstOrDefault(c => c.Value.Type == "http-01").Value;

                    //if copying the file for the user, attempt that now
                    if (challengeInfo != null && requestConfig.PerformChallengeFileCopy)
                    {
                        var httpChallenge = (ACMESharp.ACME.HttpChallenge)challengeInfo.Challenge;
                        this.LogAction("Preparing challenge response for LetsEncrypt server to check at: " + httpChallenge.FileUrl);
                        this.LogAction("If the challenge response file is not accessible at this exact URL the validation will fail and a certificate will not be issued.");

                        //copy temp file to path challenge expects in web folder
                        var destFile = Path.Combine(requestConfig.WebsiteRootPath, httpChallenge.FilePath);
                        var destPath = Path.GetDirectoryName(destFile);
                        if (!Directory.Exists(destPath))
                        {
                            Directory.CreateDirectory(destPath);
                        }

                        //copy challenge response to web folder /.well-known/acme-challenge
                        System.IO.File.WriteAllText(destFile, httpChallenge.FileContent);

                        var wellknownContentPath = httpChallenge.FilePath.Substring(0, httpChallenge.FilePath.LastIndexOf("/"));
                        var testFilePath = Path.Combine(requestConfig.WebsiteRootPath, wellknownContentPath + "//configcheck");
                        System.IO.File.WriteAllText(testFilePath, "Extensionless File Config Test - OK");

                        //create a web.config for extensionless files, then test it (make a request for the extensionless configcheck file over http)
                        string webConfigContent = Properties.Resources.IISWebConfig;

                        if (!File.Exists(destPath + "\\web.config"))
                        {
                            //no existing config, attempt auto config and perform test
                            System.IO.File.WriteAllText(destPath + "\\web.config", webConfigContent);
                            if (requestConfig.PerformExtensionlessConfigChecks)
                            {
                                if (CheckURL("http://" + domain + "/" + wellknownContentPath + "/configcheck", checkViaProxy))
                                {
                                    extensionlessConfigOK = true;
                                }
                            }
                        }
                        else
                        {
                            //web config already exists, don't overwrite it, just test it

                            if (requestConfig.PerformExtensionlessConfigChecks)
                            {
                                if (CheckURL("http://" + domain + "/" + wellknownContentPath + "/configcheck", checkViaProxy))
                                {
                                    extensionlessConfigOK = true;
                                }
                                if (!extensionlessConfigOK && requestConfig.PerformExtensionlessAutoConfig)
                                {
                                    //didn't work, try our default config
                                    System.IO.File.WriteAllText(destPath + "\\web.config", webConfigContent);

                                    if (CheckURL("http://" + domain + "/" + wellknownContentPath + "/configcheck", checkViaProxy))
                                    {
                                        extensionlessConfigOK = true;
                                    }
                                }
                            }
                        }

                        if (!extensionlessConfigOK && requestConfig.PerformExtensionlessAutoConfig)
                        {
                            //if first attempt(s) at config failed, try an alternative config
                            webConfigContent = Properties.Resources.IISWebConfigAlt;

                            System.IO.File.WriteAllText(destPath + "\\web.config", webConfigContent);

                            if (CheckURL("http://" + domain + "/" + wellknownContentPath + "/configcheck", checkViaProxy))
                            {
                                //ready to complete challenge
                                extensionlessConfigOK = true;
                            }
                        }
                    }

                    return new PendingAuthorization() { Challenge = challengeInfo, Identifier = identifier, TempFilePath = "", ExtensionlessConfigCheckedOK = extensionlessConfigOK };
                }
                else
                {
                    return null;
                }
            }
            else
            {
                //identifier is already valid (previously authorized)
                return new PendingAuthorization() { Challenge = null, Identifier = identifier, TempFilePath = "", ExtensionlessConfigCheckedOK = false };
            }
        }

        #endregion Certificates

        public string ComputeIdentifierAlias(string domain)
        {
            return "ident" + Guid.NewGuid().ToString().Substring(0, 8).Replace("-", "");
            /*var domainAlias = domain.Replace(".", "_");
            domainAlias += long.Parse(DateTime.UtcNow.ToString("yyyyMMddHHmmss"));

            // Check if the first character in the domain is a digit, e.g. 1and1.com
            // Per ACMESharp spec, alias cannot begin with a digit.
            if (char.IsDigit(domainAlias[0]))
            {
                domainAlias = "alias_" + domainAlias;
            }

            return domainAlias;*/
        }

        private bool CheckURL(string url, bool useProxyAPI)
        {
            var checkUrl = url + "";
            if (useProxyAPI)
            {
                url = "https://certify.webprofusion.com/api/testurlaccess?url=" + url;
            }
            //check http request to test path works
            bool checkSuccess = false;
            try
            {
                WebRequest request = WebRequest.Create(url);
                var response = (HttpWebResponse)request.GetResponse();

                //if checking via proxy, examine result
                if (useProxyAPI)
                {
                    if ((int)response.StatusCode >= 200)
                    {
                        var encoding = ASCIIEncoding.UTF8;
                        using (var reader = new System.IO.StreamReader(response.GetResponseStream(), encoding))
                        {
                            string jsonText = reader.ReadToEnd();
                            this.LogAction("URL Check Result: " + jsonText);
                            var result = Newtonsoft.Json.JsonConvert.DeserializeObject<Models.API.URLCheckResult>(jsonText);
                            if (result.IsAccessible == true)
                            {
                                checkSuccess = true;
                            }
                            else
                            {
                                checkSuccess = false;
                            }
                        }
                    }
                }
                else
                {
                    //not checking via proxy, base result on status code
                    if ((int)response.StatusCode >= 200 && (int)response.StatusCode < 300)
                    {
                        checkSuccess = true;
                    }
                }

                if (checkSuccess == false && useProxyAPI == true)
                {
                    //request failed using proxy api, request again using local http
                    checkSuccess = CheckURL(checkUrl, false);
                }
            }
            catch (Exception)
            {
                System.Diagnostics.Debug.WriteLine("Failed to check url for access");
                checkSuccess = false;
            }

            return checkSuccess;
        }

        public void SubmitChallenge(string alias, string challengeType = "http-01")
        {
            //well known challenge all ready to be read by server
            powershellManager.SubmitChallenge(alias, challengeType);

            UpdateIdentifierStatus(alias, challengeType);
        }

        public void UpdateIdentifierStatus(string alias, string challengeType = "http-01")
        {
            powershellManager.UpdateIdentifier(alias, challengeType);
        }

        public string GetActionLogSummary()
        {
            string output = "";
            if (this.ActionLogs != null)
            {
                foreach (var a in this.ActionLogs)
                {
                    output += a.ToString() + "\r\n";
                }
            }
            return output;
        }

        public void PermissionTest()
        {
            if (IisSitePathProvider.IsAdministrator())
            {
                System.Diagnostics.Debug.WriteLine("User is an administrator");

                var iisPathProvider = new IisSitePathProvider();
                iisPathProvider.WebSiteRoot = @"C:\inetpub\wwwroot\";
                using (var fs = File.OpenRead(@"C:\temp\log.txt"))
                {
                    var fileURI = new System.Uri(iisPathProvider.WebSiteRoot + "/.temp/test/test123");
                    iisPathProvider.UploadFile(fileURI, fs);
                }
            }
            else
            {
                System.Diagnostics.Debug.WriteLine("User is not an administrator");
            }
        }
    }
}