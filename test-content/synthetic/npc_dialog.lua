-- npc_dialog.lua: Synthetic test profile: invented content written by `dotnet run --project Tools -- synthetic-profile`. Edit the generator, not this file.
-- No dialogs. The loader (Server/NpcScript.cs) accepts the file once it defines a global `npcs` table
-- and a `__make_ctx` function; with `npcs` empty it never calls `__make_ctx`.
npcs = {}
function __make_ctx() return {} end
