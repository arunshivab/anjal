# CertificateStore

**Namespace:** `Anjal.Acme`

PEM files in one directory: account.key.pem, cert.key.pem, fullchain.pem, meta.json. Certificates are written to a temp file and renamed so a reader never sees a partial file. Any process that can read the directory can load the certificate; only the process hosting renewal writes it.

## Members

- **#ctor** *(method)* - Construct.
- **CreateCertificateKey** *(method)* - Generate a fresh certificate key of the given type (not saved until ).
- **LoadCertificate** *(method)* - Load the current certificate with its private key. Returns null if no certificate is stored or the files cannot be parsed. The returned certificate is exportable and usable by Kestrel and SslStream on all platforms.
- **LoadChain** *(method)* - Load the intermediate certificates from the chain (everything after the leaf).
- **LoadMetadata** *(method)* - Load metadata, or null.
- **LoadOrCreateAccountKey** *(method)* - Load the account key, creating and saving one if absent.
- **ReadStatus** *(method)* - Read the last written status, or null.
- **RequestRenewal** *(method)* - Ask the renewal service (which may be another process) to renew at its next check regardless of expiry, by dropping a marker file.
- **Save** *(method)* - Save a newly issued certificate and its key atomically.
- **TakeRenewalRequest** *(method)* - Consume the renew-now marker. Returns true if one was present.
- **WriteStatus** *(method)* - Write the renewal service status for other processes to read.
- **AccountKeyPath** *(property)* - Path of the account key PEM.
- **CertificateKeyPath** *(property)* - Path of the certificate private key PEM.
- **CertificateWrittenAtUtc** *(property)* - Last write time of the chain file (UTC), or null if none.
- **Directory** *(property)* - The directory.
- **FullChainPath** *(property)* - Path of the full chain PEM (leaf first).
- **HasCertificate** *(property)* - Whether a certificate has been saved.
- **MetadataPath** *(property)* - Path of the metadata JSON.
- **RenewRequestPath** *(property)* - Path of the renew-now request marker (see ).
- **StatusPath** *(property)* - Path of the status JSON written by the renewal service.
