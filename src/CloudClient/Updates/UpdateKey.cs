namespace CloudClient.Updates;

/// <summary>
/// The publisher's public key (ECDSA P-256, CNG blob). Updates are installed
/// only when their Signature.txt was made with the matching private key, which
/// never leaves the publisher's machine (see scripts/new-signing-key.ps1).
/// </summary>
public static class UpdateKey
{
    public const string Public = "RUNTMSAAAAC+p3RHPfbYlqFESOgWwvVi6B/WhBFfgdWPoVwkclmdryyPo9C0rU5o2G4rhQQUJgcC9Q+j4rksv6Frba8Atpwr";
}
