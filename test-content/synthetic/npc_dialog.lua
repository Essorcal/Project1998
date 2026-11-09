-- npc_dialog.lua: Synthetic test profile: invented content written by `dotnet run --project Tools -- synthetic-profile`. Edit the generator, not this file.
-- The loader (Server/NpcScript.cs) runs npcs.<NpcIdentifier>(ctx) as a coroutine when that NPC is clicked.
-- Each ctx method yields one request, and the server resumes the coroutine with the player's 0x3A reply:
--   ctx:say(text)               a text box with a next arrow; returns after the next
--   ctx:menu(prompt, entries)   a menu; returns the 1-based pick, or 0 for a reply that is not a pick
--   ctx:input(prompt)           an input box; returns the submitted text, or nil for any other reply
-- manifest.json describes each dialog under its NPC's entity; both are written from the same strings.

function __make_ctx()
  local ctx = {}
  function ctx:say(...)                 return coroutine.yield({op="say", pages={...}}) end
  function ctx:menu(prompt, options)    return coroutine.yield({op="menu", prompt=prompt, options=options}) end
  function ctx:input(prompt)            return coroutine.yield({op="input", prompt=prompt}) end
  return ctx
end

npcs = {}

-- Synthetic Clerk (NPC 1001). Any reply to the menu that leads nowhere ends the conversation.
function npcs.SyntheticClerk(ctx)
  local pick = ctx:menu("Synthetic Clerk: pick a test.", {"Type a line", "Read the notice", "Goodbye"})
  if pick == 1 then
    local typed = ctx:input("Type a line for the clerk.")
    if typed then
      ctx:say("You typed " .. #typed .. " characters: " .. typed)
    else
      ctx:say("Nothing was submitted.")
    end
  elseif pick == 2 then
    ctx:say("The notice is blank. It is a synthetic notice.")
  end
end
