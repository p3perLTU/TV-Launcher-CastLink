// HTTPS for the iPad page without a public certificate: this PC makes its own small
// certificate authority (once) and, from it, a server certificate for its current LAN
// addresses. The iPad installs the CA once (a profile downloaded from http://<PC>:47900/ca.cer)
// and then trusts https://<PC>:47900, which makes the page a "secure context": Safari only then
// allows the Screen Wake Lock (the screen stays on) and other modern APIs.
//
// Safety: the CA carries a critical Name Constraints extension: it can only vouch for private
// IPv4 addresses (the ranges reserved for home networks, and this PC) and for DNS names under
// ".invalid" (i.e. none). Even with its private key nobody could pose as a real website to the
// iPad. The key is kept in %APPDATA%\TVCast\local-ca.dat, encrypted with Windows DPAPI for this
// Windows user only. Each PC makes its own CA.
//
// The DER builders are pure and unit tested (tests/cs).

using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace TVCast
{
    static class LocalTls
    {
        public const string CaName = "TV Cast local CA";
        static readonly byte[] Entropy = Encoding.ASCII.GetBytes("TVCast local CA v1");
        static readonly object gate = new object();
        static X509Certificate2 ca, leaf;
        static List<string> leafIps = new List<string>();

        public static string CaFile
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "TVCast", "local-ca.dat"); }
        }

        /// <summary>the CA certificate (public part), for the iPad to install</summary>
        public static byte[] CaCertificate()
        {
            lock (gate) return Ca().RawData;
        }

        /// <summary>
        /// The first 8 bytes of the CA's SHA-256 fingerprint as the iPad shows them (Settings ->
        /// General -> VPN & Device Management -> the profile -> More Details). The certificate is
        /// downloaded over plain HTTP, so someone on the Wi-Fi could swap it for their own
        /// (unconstrained) one; comparing this with the iPad before trusting it rules that out.
        /// </summary>
        public static string CaFingerprint()
        {
            return Fingerprint(CaCertificate());
        }

        internal static string Fingerprint(byte[] der)
        {
            using (SHA256 sha = SHA256.Create())
            {
                byte[] h = sha.ComputeHash(der);
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < 8; i++) sb.Append(i > 0 ? " " : "").Append(h[i].ToString("X2"));
                return sb.ToString();
            }
        }

        /// <summary>A server certificate valid for the address the iPad connected to; made again
        /// (from the same CA, nothing to reinstall) when a new address turns up, e.g. the Windows
        /// hotspot was switched on.</summary>
        public static X509Certificate2 ServerCertificate(IPAddress local)
        {
            lock (gate)
            {
                string ip = local.ToString();
                if (leaf == null || !leafIps.Contains(ip) || leaf.NotAfter < DateTime.Now.AddDays(1))
                {
                    List<string> ips = CurrentAddresses();
                    if (!ips.Contains(ip)) ips.Add(ip);
                    leaf = CreateLeaf(Ca(), ips);
                    leafIps = ips;
                }
                return leaf;
            }
        }

        static List<string> CurrentAddresses()
        {
            List<string> ips = new List<string> { "127.0.0.1" };
            foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != OperationalStatus.Up) continue;
                foreach (UnicastIPAddressInformation a in ni.GetIPProperties().UnicastAddresses)
                {
                    if (a.Address.AddressFamily != AddressFamily.InterNetwork || !Proto.IsLanV4(a.Address.GetAddressBytes())) continue;
                    string s = a.Address.ToString();
                    if (!ips.Contains(s)) ips.Add(s);
                }
            }
            return ips;
        }

        static X509Certificate2 Ca()
        {
            if (ca != null) return ca;
            string file = CaFile;
            if (File.Exists(file))
            {
                try
                {
                    byte[] pfx = ProtectedData.Unprotect(File.ReadAllBytes(file), Entropy, DataProtectionScope.CurrentUser);
                    ca = new X509Certificate2(pfx, (string)null, X509KeyStorageFlags.Exportable);
                    if (ca.HasPrivateKey && ca.NotAfter > DateTime.Now.AddDays(400)) return ca;
                }
                catch (CryptographicException) { } // another Windows user's file, or damaged: make a new CA
            }
            using (RSA rsa = new RSACng(3072))
            {
                CertificateRequest req = new CertificateRequest(new X500DistinguishedName("CN=" + CaName + ", O=CastLink"),
                    rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                req.CertificateExtensions.Add(new X509BasicConstraintsExtension(true, true, 0, true));
                req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.KeyCertSign | X509KeyUsageFlags.CrlSign, true));
                req.CertificateExtensions.Add(new X509SubjectKeyIdentifierExtension(req.PublicKey, false));
                req.CertificateExtensions.Add(new X509Extension("2.5.29.30", NameConstraints(), true));
                using (X509Certificate2 made = req.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddYears(10)))
                {
                    byte[] pfx = made.Export(X509ContentType.Pkcs12);
                    Directory.CreateDirectory(Path.GetDirectoryName(file));
                    File.WriteAllBytes(file, ProtectedData.Protect(pfx, Entropy, DataProtectionScope.CurrentUser));
                    ca = new X509Certificate2(pfx, (string)null, X509KeyStorageFlags.Exportable);
                }
            }
            return ca;
        }

        static X509Certificate2 CreateLeaf(X509Certificate2 issuer, List<string> ips)
        {
            using (RSA rsa = new RSACng(2048))
            {
                // no common name: only the IP addresses in the SAN, all inside the CA's constraints
                CertificateRequest req = new CertificateRequest(new X500DistinguishedName("O=CastLink"), rsa,
                    HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
                req.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
                req.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature | X509KeyUsageFlags.KeyEncipherment, true));
                req.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(new OidCollection { new Oid("1.3.6.1.5.5.7.3.1") }, false));
                SubjectAlternativeNameBuilder san = new SubjectAlternativeNameBuilder();
                foreach (string ip in ips) san.AddIpAddress(IPAddress.Parse(ip));
                req.CertificateExtensions.Add(san.Build(false));
                X509SubjectKeyIdentifierExtension caSki = (X509SubjectKeyIdentifierExtension)issuer.Extensions["2.5.29.14"];
                if (caSki != null) req.CertificateExtensions.Add(new X509Extension("2.5.29.35", AuthorityKeyId(HexBytes(caSki.SubjectKeyIdentifier)), false));
                byte[] serial = new byte[16];
                using (RandomNumberGenerator r = RandomNumberGenerator.Create()) r.GetBytes(serial);
                serial[0] &= 0x7F;
                // Apple accepts at most 825 days for TLS certificates from installed roots; 397 is safe
                DateTimeOffset until = DateTimeOffset.UtcNow.AddDays(397);
                if (until > issuer.NotAfter) until = issuer.NotAfter.AddDays(-1);
                using (X509Certificate2 pub = req.Create(issuer, DateTimeOffset.UtcNow.AddDays(-1), until, serial))
                using (X509Certificate2 withKey = pub.CopyWithPrivateKey(rsa))
                {
                    // through a PFX: SChannel (SslStream) cannot use the in-memory key directly
                    return new X509Certificate2(withKey.Export(X509ContentType.Pkcs12), (string)null, X509KeyStorageFlags.Exportable);
                }
            }
        }

        // ---- DER (pure) -----------------------------------------------------------------------

        /// <summary>NameConstraints with permitted subtrees: the private IPv4 ranges and the DNS
        /// name "invalid" (so no real host name is permitted)</summary>
        internal static byte[] NameConstraints()
        {
            MemoryStream subtrees = new MemoryStream();
            byte[][] ranges = {
                new byte[] { 10, 0, 0, 0, 255, 0, 0, 0 },
                new byte[] { 172, 16, 0, 0, 255, 240, 0, 0 },
                new byte[] { 192, 168, 0, 0, 255, 255, 0, 0 },
                new byte[] { 169, 254, 0, 0, 255, 255, 0, 0 },
                new byte[] { 127, 0, 0, 0, 255, 0, 0, 0 } };
            foreach (byte[] r in ranges) Write(subtrees, Tlv(0x30, Tlv(0x87, r)));            // GeneralSubtree { iPAddress }
            Write(subtrees, Tlv(0x30, Tlv(0x82, Encoding.ASCII.GetBytes("invalid"))));       // GeneralSubtree { dNSName }
            return Tlv(0x30, Tlv(0xA0, subtrees.ToArray()));                                  // NameConstraints { permittedSubtrees }
        }

        internal static byte[] AuthorityKeyId(byte[] keyId)
        {
            return Tlv(0x30, Tlv(0x80, keyId));
        }

        internal static byte[] Tlv(byte tag, byte[] value)
        {
            MemoryStream m = new MemoryStream();
            m.WriteByte(tag);
            int n = value.Length;
            if (n < 0x80) m.WriteByte((byte)n);
            else if (n < 0x100) { m.WriteByte(0x81); m.WriteByte((byte)n); }
            else { m.WriteByte(0x82); m.WriteByte((byte)(n >> 8)); m.WriteByte((byte)n); }
            Write(m, value);
            return m.ToArray();
        }

        static void Write(Stream s, byte[] b) { s.Write(b, 0, b.Length); }

        static byte[] HexBytes(string hex)
        {
            byte[] b = new byte[hex.Length / 2];
            for (int i = 0; i < b.Length; i++) b[i] = Convert.ToByte(hex.Substring(i * 2, 2), 16);
            return b;
        }
    }
}
