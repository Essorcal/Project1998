namespace Shared;

/// <summary>
/// NexusTK 4.95 protocol opcodes, observed live from the client.
/// (Reusable protocol constants live in Shared so a future Unity client can reference them.)
/// </summary>
public static class Opcode
{
    // ---- login server (client -> server) ----
    public const byte Signature        = 0x62; // "baram" client signature
    public const byte Version          = 0x00; // client version
    public const byte NameCheck        = 0x02; // create step 1 (name availability)
    public const byte Login            = 0x03; // login with credentials
    public const byte CreateAppearance = 0x04; // create step 2 (face/hair/sex/totem)
    public const byte ChangePassword   = 0x26; // login-screen password change: name + old + new (5.33 observed; RTK login clif.c case 0x26)

    // ---- game server ----
    public const byte Arrival          = 0x10; // client arrives with handoff token (plaintext)
    public const byte ExitToSelect     = 0x0B; // client -> game: "I left the world for the select screen"
    public const byte MapInfo          = 0x15; // server -> client: load map
}
