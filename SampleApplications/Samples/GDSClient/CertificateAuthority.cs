/* ========================================================================
 * Copyright (c) 2005-2011 The OPC Foundation, Inc. All rights reserved.
 *
 * OPC Reciprocal Community License ("RCL") Version 1.00
 * 
 * Unless explicitly acquired and licensed from Licensor under another 
 * license, the contents of this file are subject to the Reciprocal 
 * Community License ("RCL") Version 1.00, or subsequent versions 
 * as allowed by the RCL, and You may not copy or use this file in either 
 * source code or executable form, except in compliance with the terms and 
 * conditions of the RCL.
 * 
 * All software distributed under the RCL is provided strictly on an 
 * "AS IS" basis, WITHOUT WARRANTY OF ANY KIND, EITHER EXPRESS OR IMPLIED, 
 * AND LICENSOR HEREBY DISCLAIMS ALL SUCH WARRANTIES, INCLUDING WITHOUT 
 * LIMITATION, ANY WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR 
 * PURPOSE, QUIET ENJOYMENT, OR NON-INFRINGEMENT. See the RCL for specific 
 * language governing rights and limitations under the RCL.
 *
 * The complete license agreement can be found here:
 * http://opcfoundation.org/License/RCL/1.00/
 * ======================================================================*/

using Org.BouncyCastle.Asn1.X509;
using Org.BouncyCastle.Crypto;
using Org.BouncyCastle.Crypto.Operators;
using Org.BouncyCastle.Crypto.Parameters;
using Org.BouncyCastle.Math;
using Org.BouncyCastle.OpenSsl;
using Org.BouncyCastle.Pkcs;
using Org.BouncyCastle.Security;
using System;
using System.IO;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;

namespace Opc.Ua
{
    /// <summary>
    /// Creates a manages certificates.
    /// </summary>
    public class CertificateAuthority
    {
        /// <summary>
        /// Combines the public key of one cert with the private key of another.
        /// </summary>
        public static X509Certificate2 Combine(
            X509Certificate2 publicKeyCertificate,
            X509Certificate2 privateKeyCertificate)
        {
            using (var cfrg = new CertificateFactoryRandomGenerator())
            {
                // cert generators
                SecureRandom random = new SecureRandom(cfrg);

                AsymmetricKeyParameter privateKey = null;
                using (RSA rsa = privateKeyCertificate.GetRSAPrivateKey())
                {
                    RSAParameters rsaParams = rsa.ExportParameters(true);
                    RsaPrivateCrtKeyParameters keyParams = new RsaPrivateCrtKeyParameters(
                        new BigInteger(1, rsaParams.Modulus),
                        new BigInteger(1, rsaParams.Exponent),
                        new BigInteger(1, rsaParams.D),
                        new BigInteger(1, rsaParams.P),
                        new BigInteger(1, rsaParams.Q),
                        new BigInteger(1, rsaParams.DP),
                        new BigInteger(1, rsaParams.DQ),
                        new BigInteger(1, rsaParams.InverseQ));
                    privateKey = keyParams;
                }

                // create pkcs12 store for cert and private key
                using (MemoryStream pfxData = new MemoryStream(publicKeyCertificate.Export(X509ContentType.Pkcs12)))
                {
                    Pkcs12Store pkcsStore = new Pkcs12StoreBuilder().Build();
                    pkcsStore.Load(pfxData, null);
                    X509CertificateEntry[] chain = new X509CertificateEntry[1];
                    string passcode = Guid.NewGuid().ToString();
                    chain[0] = pkcsStore.GetCertificate("alias");
                    pkcsStore.SetKeyEntry(publicKeyCertificate.Subject, new AsymmetricKeyEntry(privateKey), chain);
                    pkcsStore.Save(pfxData, passcode.ToCharArray(), random);

                    // merge into X509Certificate2
                    return CertificateFactory.CreateCertificateFromPKCS12(pfxData.ToArray(), passcode);
                }
            }
        }

        /// <summary>
        /// Creates a certificate signing request.
        /// </summary>
        /// <param name="certificate">The certificate to go with the private key.</param>
        /// <param name="privateKey">The private key used to sign the request.</param>
        /// <param name="isPEMKey">TRUE if the private key is in PEM format; FALSE otherwise.</param>
        /// <param name="password">The password for the private key.</param>
        /// <param name="subjectName">Subject name for the new certificate.</param>
        /// <param name="applicationUri">The application uri. Replaces whatever is in the existing certificate.</param>
        /// <param name="domainNames">The domain names. Replaces whatever is in the existing certificate.</param>
        /// <param name="hashSizeInBits">The hash size in bits.</param>
        /// <returns>
        /// The certificate signing request.
        /// </returns>
        public static byte[] CreateRequest(
            X509Certificate2 certificate,
            byte[] privateKey,
            bool isPEMKey,
            ushort hashSizeInBits)
        {
            using (var cfrg = new CertificateFactoryRandomGenerator())
            {
                SecureRandom random = new SecureRandom(cfrg);
               
                AsymmetricKeyParameter signingKey = null;
                if (isPEMKey)
                {
                    TextReader textReader = new StringReader(new string(Encoding.ASCII.GetChars(privateKey)));
                    PemReader pemReader = new PemReader(textReader);
                    AsymmetricCipherKeyPair keys = (AsymmetricCipherKeyPair)pemReader.ReadObject();
                    signingKey = keys.Private;
                }
                else
                {
                    X509Certificate2 temp = null;
                    if (signingKey == null)
                    {
                        // try to get signing/private key from certificate passed in
                        temp = certificate;
                    }
                    else
                    {
                        // PFX
                        temp = new X509Certificate2(privateKey, string.Empty, X509KeyStorageFlags.Exportable);
                    }
                    using (RSA rsa = temp.GetRSAPrivateKey())
                    {
                        RSAParameters rsaParams = rsa.ExportParameters(true);
                        RsaPrivateCrtKeyParameters keyParams = new RsaPrivateCrtKeyParameters(
                            new BigInteger(1, rsaParams.Modulus),
                            new BigInteger(1, rsaParams.Exponent),
                            new BigInteger(1, rsaParams.D),
                            new BigInteger(1, rsaParams.P),
                            new BigInteger(1, rsaParams.Q),
                            new BigInteger(1, rsaParams.DP),
                            new BigInteger(1, rsaParams.DQ),
                            new BigInteger(1, rsaParams.InverseQ));
                        signingKey = keyParams;
                    }
                }

                RsaKeyParameters publicKey = null;
                using (RSA rsa = certificate.GetRSAPublicKey())
                {
                    RSAParameters rsaParams = rsa.ExportParameters(false);
                    publicKey = new RsaKeyParameters(
                        false,
                        new BigInteger(1, rsaParams.Modulus),
                        new BigInteger(1, rsaParams.Exponent));
                }

                ISignatureFactory signatureFactory =
                new Asn1SignatureFactory((hashSizeInBits < 256) ? "SHA1WITHRSA" : "SHA256WITHRSA", signingKey, random);

                Pkcs10CertificationRequest pkcs10CertificationRequest = new Pkcs10CertificationRequest(
                    signatureFactory,
                    new X509Name(true, certificate.Subject.Replace("S=", "ST=")),
                    publicKey,
                    null,
                    signingKey);

                return pkcs10CertificationRequest.GetEncoded();
            }
        }
    }
}