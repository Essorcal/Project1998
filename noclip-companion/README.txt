========================================================================
  NexusTK No-Clip Companion  (for testers)
========================================================================

WHAT THIS IS
------------
A little helper program that lets you walk through walls, buildings and
fences while testing. It works together with the "@clip" chat command:
you type @clip in game, and this companion flips walls on/off to match.

It changes NOTHING on your computer or your game files. It only takes
effect while its window is open -- close the window and your game is
back to completely normal.


BEFORE YOU START -- 3 things you need
-------------------------------------
1. The SAME NexusTK client you were given for this server. (This tool is
   built for that exact client. A different download will not work and may
   crash the game.)

2. Python 3 installed on your PC. If you don't have it:
   - Get it from  https://www.python.org/downloads/
   - On the FIRST install screen, TICK the box "Add Python to PATH".

3. The server admin has to add your CHARACTER NAME to the tester list.
   Ask them to do this. Without it, "@clip" won't do anything for you.


FIRST-TIME SETUP  (do this once)
--------------------------------
Double-click:   Install-Once.bat

It installs the one component the companion needs ("Frida"). Wait for it
to say "Done", then close it. You never need to run it again.


HOW TO USE IT  (every time you want no-clip)
--------------------------------------------
1. Start NexusTK and log in like normal.
2. Double-click:   Start-NoClip.bat
   A small black window opens and says it "attached" to the game.
   Leave that window open.
3. In game, type:   @clip
   Walls open up. Type @clip again to turn it back off.
4. When you're done, just close the black window. Your game goes back to
   normal collision instantly.

Tip: you can start Start-NoClip.bat before OR after logging in -- it waits
for the game and attaches on its own. If you restart the game, it
re-attaches automatically.


IF SOMETHING DOESN'T WORK
-------------------------
* Typing @clip says "Unknown command"
    -> Your name isn't on the tester list yet. Ask the server admin to add
       your character name, then relog.

* @clip toggles (you see the on/off message) but walls still block
    -> The companion window isn't running, or it didn't attach. Start
       Start-NoClip.bat and check it says "attached to NexusTK.exe".

* Antivirus / Windows Defender blocks or deletes it
    -> This tool "attaches" to the game the way debuggers do, which some
       antivirus flags. You may need to allow it / add an exception. It is
       safe -- it doesn't modify any files.

* The game crashes when you toggle, or nothing happens at all
    -> You're probably running a different client version than the one this
       was built for. Use the client you were given for this server.

* Start-NoClip.bat says "Python was NOT found"
    -> Install Python 3 (see "BEFORE YOU START" above), making sure to tick
       "Add Python to PATH", then run Install-Once.bat.


IS THIS CHEATING? / CAN OTHER PLAYERS USE IT?
---------------------------------------------
No. No-clip only actually works when the SERVER lets you through, and that
is turned on by the staff-only "@clip" command. On its own this companion
does nothing that would let a normal player pass real walls -- the server
still stops them. It's a testing convenience, not an exploit.


WHAT'S IN THIS FOLDER
---------------------
  Start-NoClip.bat        <- run this to play with no-clip
  Install-Once.bat        <- run this once, the first time
  frida_noclip_533.py     <- the companion itself (don't need to open it)
  README.txt              <- this file
