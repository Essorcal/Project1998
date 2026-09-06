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

/// <summary>Client-to-server opcodes. Names preserve the existing dispatch meanings; see Session.Dispatch for provenance.</summary>
public static class ClientOp
{
    public const byte Version = 0x00;
    public const byte NameCheck = 0x02;
    public const byte Login = 0x03;
    public const byte CreateAppearance = 0x04;
    public const byte MapRequest = 0x05;
    public const byte Walk = 0x06;
    public const byte Pickup = 0x07;
    public const byte DropItem = 0x08;
    public const byte LookAt = 0x09;
    public const byte ExitToSelect = 0x0B;
    public const byte Chat = 0x0E;
    public const byte Cast = 0x0F;
    public const byte Arrival = 0x10;
    public const byte Turn = 0x11;
    public const byte Wield = 0x12;
    public const byte Attack = 0x13;
    public const byte Throw = 0x17;
    public const byte UserList = 0x18;
    public const byte Whisper = 0x19;
    public const byte Eat = 0x1A;
    public const byte Setting = 0x1B;
    public const byte UseItem = 0x1C;
    public const byte Emotion = 0x1D;
    public const byte Unequip = 0x1F;
    public const byte Open = 0x20;
    public const byte DropGold = 0x24;
    public const byte ChangePassword = 0x26;
    public const byte HandItem = 0x29;
    public const byte HandGold = 0x2A;
    public const byte ProfileRequest = 0x2D;
    public const byte PartyInvite = 0x2E;
    public const byte ChangePos = 0x30;
    public const byte WalkAlternate = 0x32;
    public const byte Refresh = 0x38;
    public const byte ShopReply = 0x39;
    public const byte NpcDialog = 0x3A;
    public const byte Board = 0x3B;
    public const byte WorldMapSelect = 0x3F;
    public const byte Parcel = 0x41;
    public const byte ClickInfo = 0x43;
    public const byte Exchange = 0x4A;
    public const byte ChangeProfile = 0x4F;
    public const byte Signature = 0x62;
    public const byte ItemOrTownInfo = 0x66;
}

/// <summary>Server-to-client game opcodes emitted by the current builders. Direction matters: the same number can mean something else inbound.</summary>
public static class ServerOp
{
    public const byte Message = 0x02;
    public const byte Position = 0x04;
    public const byte PlayerId = 0x05;
    public const byte MapCells = 0x06;
    public const byte CreatureList = 0x07;
    public const byte Stats = 0x08;
    public const byte MiniText = 0x0A;
    public const byte Move = 0x0C;
    public const byte Speech = 0x0D;
    public const byte Despawn = 0x0E;
    public const byte AddItem = 0x0F;
    public const byte DeleteItem = 0x10;
    public const byte Turn = 0x11;
    public const byte Damage = 0x13;
    public const byte MapInfo = 0x15;
    public const byte FloorItem = 0x16;
    public const byte AddSpell = 0x17;
    public const byte RemoveSpell = 0x18;
    public const byte Audio = 0x19;
    public const byte Action = 0x1A;
    public const byte LookUpdate = 0x1D;
    public const byte Ack = 0x1E;
    public const byte Weather = 0x1F;
    public const byte Time = 0x20;
    public const byte MapDone = 0x22;
    public const byte Options = 0x23;
    public const byte SelfWalk = 0x26;
    public const byte Effect = 0x29;
    public const byte WorldMap = 0x2E;
    public const byte Shop = 0x2F;
    public const byte NpcDialog = 0x30;
    public const byte Board = 0x31;
    public const byte PlayerLook = 0x33;
    public const byte PeerProfile = 0x34;
    public const byte UserList = 0x36;
    public const byte Equip = 0x37;
    public const byte Unequip = 0x38;
    public const byte SelfProfile = 0x39;
    public const byte Exchange = 0x42;
    public const byte ProfilePictureRequest = 0x49;
    public const byte TooltipOrTownList = 0x59;
    public const byte ItemInfo = 0x66;
}
