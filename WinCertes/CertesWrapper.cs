﻿using Certes;
using Certes.Acme;
using Certes.Acme.Resource;
using NLog;
using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Threading.Tasks;
using WinCertes.ChallengeValidator;

namespace WinCertes
{
    /// <summary>
    /// Commodity class to store Certes settings
    /// </summary>
    public class CertesSettings
    {
        public Uri ServiceURI { get; set; }
        public string AccountEmail { get; set; }
        public IKey AccountKey { get; set; }
    }

    /// <summary>
    /// CertesWrapper class: a wrapper around Certes library, that simplifies handling ACME requests in the context of WinCertes
    /// </summary>
    /// <seealso cref="Certes"/>
    public class CertesWrapper
    {
        public string PfxPassword { get; set; }
        private static readonly ILogger logger = LogManager.GetLogger("WinCertes.CertesWrapper");
        private IConfig _config;
        private CertesSettings _settings;
        private AcmeContext _acme;
        private IOrderContext _orderCtx = null;

        /// <summary>
        /// Initializes Certes library context
        /// </summary>
        private void InitCertes()
        {
            _acme = new AcmeContext(_settings.ServiceURI, _settings.AccountKey);
        }

        /// <summary>
        /// CertesWrapper class constructor
        /// </summary>
        /// <param name="serviceUri">The ACME service URI (endin in /directory). If null, defaults to Let's encrypt</param>
        /// <param name="accountEmail">The email address to be registered within the ACME account. If null, no email will be used</param>
        public CertesWrapper(string serviceUri = null, string accountEmail = null)
        {
            _settings = new CertesSettings();
            _config = new RegistryConfig();

            // Let's initialize the password
            PfxPassword = Guid.NewGuid().ToString("N").Substring(0, 16);
            logger.Debug($"PFX password will be: {PfxPassword}");

            // Dealing with Server URI
            if (serviceUri != null) {
                _settings.ServiceURI = new Uri(serviceUri);
            } else {
                _settings.ServiceURI = WellKnownServers.LetsEncryptV2;
            }
            // Dealing with account email
            _settings.AccountEmail = accountEmail;
            // Dealing with key
            if (_config.ReadStringParameter("accountKey") != null) {
                _settings.AccountKey = KeyFactory.FromPem(_config.ReadStringParameter("accountKey"));
            } else {
                _settings.AccountKey = KeyFactory.NewKey(KeyAlgorithm.RS256);
                _config.WriteStringParameter("accountKey", _settings.AccountKey.ToPem());
            }
        }

        /// <summary>
        /// Is the ACME account set on this computer registered into the ACME service ?
        /// </summary>
        /// <returns>true if registered, false otherwise</returns>
        public bool IsAccountRegistered()
        {
            return (_config.ReadIntParameter("registered") == 1);
        }

        /// <summary>
        /// Fetches the useful error messages from within the exceptions stack within Certes
        /// </summary>
        /// <param name="exp">the exception to process</param>
        /// <returns>the error messages concatenated as string</returns>
        private string ProcessCertesException(Exception exp)
        {
            string errorMessage = exp.Message;
            if (exp.InnerException != null) {
                errorMessage += " - " + exp.InnerException.Message;
                if (exp.InnerException.InnerException != null) errorMessage += " - " + exp.InnerException.InnerException.Message;
            }
            return errorMessage;
        }

        /// <summary>
        /// Register the local computer's account on the ACME service
        /// </summary>
        /// <returns>true if registration is successful, false otherwise</returns>
        public async Task<bool> RegisterNewAccount()
        {
            try {
                InitCertes();
                Certes.Acme.Resource.Directory directory = await _acme.GetDirectory();
                InitCertes();
                IAccountContext accountCtx = await _acme.NewAccount(_settings.AccountEmail, true);
                _config.WriteIntParameter("registered", 1);
                logger.Info($"Successfully registered account {_settings.AccountEmail} with certificate authority {_settings.ServiceURI.ToString()}");
                if (directory.Meta.TermsOfService != null) logger.Info($"Please check the ACME Service ToS at: {directory.Meta.TermsOfService.ToString()}");
                return true;
            } catch (Exception exp) {
                logger.Error($"Failed to register account {_settings.AccountEmail} with certificate authority {_settings.ServiceURI.ToString()}: {ProcessCertesException(exp)}");
                return false;
            }
        }

        /// <summary>
        /// Register a new order on the ACME service, for the specified domains. Challenges will be automatically verified.
        /// This method manages automatically the creation of necessary directory and files.
        /// </summary>
        /// <remarks>
        /// When using HTTP Validation, the ACME directory will access to http://__domain__/.well-known/acme-challenge/token, that should be served 
        /// by a local web server when not using built-in, and translated into local path {challengeVerifyPath}\.well-known\acme-challenge\token.
        /// Important Note: currently WinCertes supports only http-01 validation mode, and dns-01 validation mode with limitations.
        /// </remarks>
        /// <param name="domains">The list of domains to be registered and validated</param>
        /// <param name="challengeValidator">The object used for challenge validation</param>
        /// <returns></returns>
        public async Task<bool> RegisterNewOrderAndVerify(IList<string> domains, IHTTPChallengeValidator challengeValidator)
        {
            try {
                // Re-init to be sure to get a fresh Nonce
                InitCertes();

                // Creating the order
                _orderCtx = await _acme.NewOrder(domains);
                if (_orderCtx == null) throw new Exception("Could not create certificate order.");

                // And fetching authorizations
                var orderAuthz = await _orderCtx.Authorizations();

                // Looping through authorizations
                foreach (IAuthorizationContext authz in orderAuthz) {
                    InitCertes();
                    // For each authorization, get the challenges
                    var allChallenges = await authz.Challenges();
                    // Not sure if it's useful...
                    var res = await authz.Resource();
                    if (_config.ReadStringParameter("DNSServerURL") != null) {
                        // Get the DNS challenge
                        var dnsChallenge = await authz.Dns();
                        var resValidation = await ValidateDNSChallenge(dnsChallenge);
                        if (!resValidation) throw new Exception($"Could not validate challenge {dnsChallenge.Location.ToString()}");
                    } else { 
                        // Get the HTTP challenge
                        var httpChallenge = await authz.Http();
                        if (httpChallenge != null) {
                            var resValidation = await ValidateChallenge(httpChallenge, challengeValidator);
                            if (!resValidation) throw new Exception($"Could not validate challenge {httpChallenge.Location.ToString()}");
                        } else throw new Exception("Only HTTP challenges are supported for now");
                    }
                }
                // If we are here, it means order was properly created, and authorizations & challenges were properly verified.
                logger.Info($"Generated orders and validated challenges for domains: {String.Join(",", domains)}");
                return true;
            } catch (Exception exp) {
                logger.Error($"Failed to register and validate order with CA: {ProcessCertesException(exp)}");
                return false;
            }
        }

        /// <summary>
        /// Validates a DNS challenge. Similar to HTTP Validation, but different because of DNSChallenge value which is signed by account key
        /// </summary>
        /// <param name="dnsChallenge"></param>
        /// <returns></returns>
        private async Task<bool> ValidateDNSChallenge(IChallengeContext dnsChallenge)
        {
            if (dnsChallenge == null) throw new Exception("DNS Validation mode setup, but server returned no DNS challenge.");
            // We get the resource fresh
            var dnsChallengeStatus = await dnsChallenge.Resource();

            // If it's invalid, we stop right away. Should not happen, but anyway...
            if (dnsChallengeStatus.Status == ChallengeStatus.Invalid) throw new Exception("DNS challenge has an invalid status");

            // Let's prepare for ACME-DNS validation
            var dnsValue = _acme.AccountKey.DnsTxt(dnsChallenge.Token);
            bool resPrep = await PrepareDNSChallengeForValidation(dnsValue);
            if (!resPrep) return false;

            // Now let's ping the ACME service to validate the challenge token
            Challenge challengeRes = await dnsChallenge.Validate();

            // We need to loop, because ACME service might need some time to validate the challenge token
            int retry = 0;
            while (((challengeRes.Status == ChallengeStatus.Pending) || (challengeRes.Status == ChallengeStatus.Processing)) && (retry < 10)) {
                // We sleep 2 seconds between each request, to leave time to ACME service to refresh
                System.Threading.Thread.Sleep(2000);
                // We refresh the challenge object from ACME service
                challengeRes = await dnsChallenge.Resource();
                retry++;
            }

            // If challenge is Invalid, Pending or Processing, something went wrong...
            if (challengeRes.Status != ChallengeStatus.Valid) return false;

            return true;
        }

        /// <summary>
        /// Prepare for DNS validation using the ACME-DNS protocol.
        /// </summary>
        /// <param name="dnsValue"></param>
        /// <returns></returns>
        private async Task<bool> PrepareDNSChallengeForValidation(String dnsValue)
        {
            var DNSServerURL = _config.ReadStringParameter("DNSServerURL");
            var DNSServerUser = _config.ReadStringParameter("DNSServerUser");
            var DNSServerKey = _config.ReadStringParameter("DNSServerKey");
            var DNSServerSubDomain = _config.ReadStringParameter("DNSServerSubDomain");

            HttpClient client = new HttpClient();
            var content = new StringContent($"{{ \"subdomain\": \"{DNSServerSubDomain}\", \"txt\": \"{dnsValue}\" }}", Encoding.UTF8, "application/json");
            content.Headers.Add("X-Api-User", DNSServerUser);
            content.Headers.Add("X-Api-Key", DNSServerKey);

            var response = await client.PostAsync(DNSServerURL, content);
            return (response.StatusCode == System.Net.HttpStatusCode.OK);
        }

        /// <summary>
        /// Small method that validates one challenge using the specified validator
        /// </summary>
        /// <param name="httpChallenge"></param>
        /// <param name="challengeValidator"></param>
        /// <returns>true if validated, false otherwise</returns>
        private async Task<bool> ValidateChallenge(IChallengeContext httpChallenge, IHTTPChallengeValidator challengeValidator)
        {
            // We get the resource fresh
            var httpChallengeStatus = await httpChallenge.Resource();

            // If it's invalid, we stop right away. Should not happen, but anyway...
            if (httpChallengeStatus.Status == ChallengeStatus.Invalid) throw new Exception("HTTP challenge has an invalid status");

            // Else we start the challenge validation
            if (!challengeValidator.PrepareChallengeForValidation(httpChallenge.Token, httpChallenge.KeyAuthz)) return false;

            // Now let's ping the ACME service to validate the challenge token
            Challenge challengeRes = await httpChallenge.Validate();

            // We need to loop, because ACME service might need some time to validate the challenge token
            int retry = 0;
            while (((challengeRes.Status == ChallengeStatus.Pending) || (challengeRes.Status == ChallengeStatus.Processing)) && (retry < 10)) {
                // We sleep 2 seconds between each request, to leave time to ACME service to refresh
                System.Threading.Thread.Sleep(2000);
                // We refresh the challenge object from ACME service
                challengeRes = await httpChallenge.Resource();
                retry++;
            }

            // Finally we cleanup everything that was needed for validation
            challengeValidator.CleanupChallengeAfterValidation(httpChallenge.Token);

            // If challenge is Invalid, Pending or Processing, something went wrong...
            if (challengeRes.Status != ChallengeStatus.Valid) return false;

            return true;
        }

        /// <summary>
        /// Retrieves the CA chain from local computer's Root store, as a PEM chain
        /// </summary>
        /// <returns>PEM chain of local computer's CA certificates</returns>
        public byte[] GetCACertChainFromStore()
        {
            string pemBundle = "";

            pemBundle += DumpStoreContentsAsPEMBundle(StoreName.Root);
            pemBundle += DumpStoreContentsAsPEMBundle(StoreName.CertificateAuthority);

            return Encoding.UTF8.GetBytes(pemBundle);
        }

        /// <summary>
        /// Dumps the contents of a windows certificate store as a PEM bundle
        /// </summary>
        /// <param name="name">the store name</param>
        /// <returns>the PEM bundle, as a string</returns>
        private string DumpStoreContentsAsPEMBundle(StoreName name)
        {
            X509Store store = new X509Store(name, StoreLocation.LocalMachine);
            store.Open(OpenFlags.ReadOnly);
            var chain = "";
            X509Certificate2Collection certsW = store.Certificates.Find(X509FindType.FindByKeyUsage, X509KeyUsageFlags.KeyCertSign, true);

            foreach (X509Certificate2 certW in certsW) {
                StringBuilder builder = new StringBuilder();
                builder.AppendLine("-----BEGIN CERTIFICATE-----");
                builder.AppendLine(Convert.ToBase64String(certW.Export(X509ContentType.Cert), Base64FormattingOptions.InsertLineBreaks));
                builder.AppendLine("-----END CERTIFICATE-----");
                chain += builder.ToString();
            }
            store.Close();

            return chain;
        }

        /// <summary>
        /// Retrieves the certificate from the ACME service. This method also generates the key and the CSR.
        /// </summary>
        /// <param name="commonName">the CN of the certificate to be requested</param>
        /// <param name="pathForPfx">Path where the resulting PFX/PKCS#12 file will be generated</param>
        /// <param name="pfxFriendlyName">Friendly name for the resulting PFX/PKCS#12</param>
        /// <returns>The name of the generated PFX/PKCS#12 file, or null in case of error</returns>
        public async Task<string> RetrieveCertificate(IList<string> domains, string pathForPfx, string pfxFriendlyName)
        {
            try {
                if (_orderCtx == null) throw new Exception("Do not call RetrieveCertificate before RegisterNewOrderAndVerify");
                if (!System.IO.Directory.Exists(pathForPfx)) throw new Exception("Directory for PFX writing do not exists");

                InitCertes();
                // Let's generate a new key (RSA is good enough IMHO)
                IKey certKey = KeyFactory.NewKey(KeyAlgorithm.RS256);
                // Then let's generate the CSR
                var csr = await _orderCtx.CreateCsr(certKey);
                csr.AddName("CN", domains[0]);
                csr.SubjectAlternativeNames = domains;

                // and finalize the ACME order
                var finalOrder = await _orderCtx.Finalize(csr.Generate());
                // Now we can fetch the certificate
                CertificateChain cert = await _orderCtx.Download();

                // We build the PFX/PKCS#12
                var pfx = cert.ToPfx(certKey);
                pfx.AddIssuers(GetCACertChainFromStore());
                var pfxBytes = pfx.Build(pfxFriendlyName, PfxPassword);
                var pfxName = Guid.NewGuid().ToString() + ".pfx";

                // We write the PFX/PKCS#12 to file
                System.IO.File.WriteAllBytes(pathForPfx + "\\" + pfxName, pfxBytes);
                logger.Info($"Retrieved certificate from the CA. The certificate is in {pfxName}");

                return pfxName;
            } catch (Exception exp) {
                logger.Error($"Failed to retrieve certificate from CA: {ProcessCertesException(exp)}");
                return null;
            }
        }

        /// <summary>
        /// Revokes the provided certificate from the ACME Service.
        /// </summary>
        /// <param name="certificate">the certificate to revoke</param>
        /// <returns>true in case of success, false otherwise</returns>
        public async Task<bool> RevokeCertificate(X509Certificate2 certificate)
        {
            if (certificate == null) return false;
            try {
                InitCertes();

                await _acme.RevokeCertificate(certificate.RawData, RevocationReason.Unspecified, null);

                return true;
            } catch (Exception exp) {
                logger.Error($"Failed to revoke certificate with serial {certificate.GetSerialNumberString()} from CA: {ProcessCertesException(exp)}");
                return false;
            }
        }
    }
}
