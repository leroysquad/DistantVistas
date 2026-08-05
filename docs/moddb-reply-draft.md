# ModDB reply drafts

Drafts for the maintainer to post. Publishing is the maintainer's, never the assistant's.

## To Codemonkey03 (Farseer detected, mod switches itself off)

> I really love this mod. It's so cool being able to see the terrain from atop a mountain
> and pick out landmarks. On the server I'm on they're using Farseer however, with the
> previous version I was running Vintage Horizons on my client and I just turned off
> Farseer with the in-game option so they could keep using Farseer and I used VH. With the
> newest update it detects Farseer on the server and turns it's self off. Is there a way
> to force it back on?

Draft:

You found a real bug, and thank you for describing your setup so exactly. It is what let
me reproduce it in about ten minutes.

0.2.0 checks whether Farseer is loaded, which on a Farseer server is always true: those
mods are required on the client, so the game installs one for every player who joins.
Being loaded is not the same as drawing, and your setup is exactly the case that proves
it. You had Farseer switched off and this mod drawing, and my check took that away and
left you with neither. Sorry.

The next release reads Farseer's own switch. If you have turned Farseer off in its dialog,
this mod draws. There is nothing for you to set up: the setting is already saved on your
machine, so installing the update is the whole fix.

Two notes for when it lands:

- Which mod draws is decided once, at startup. If you switch Farseer off while playing,
  restart the game afterwards.
- If you ever want both to draw at once, `.vhdefer off` does that, and `.vhinfo` says what
  it is currently doing and why. I would leave it alone unless you need it, because two
  LOD mods draw over each other.

## To the "pregen did not help" report

> Terrain does not render far even after doing the pregen on the server. And what does
> render is spotty and undetailed. Typing the command .vhwhy shows me this
> [L5 nodes at distance 417 and 420 with children in "loading" and "no-data"]

Draft (**hold** until the manifest investigation is finished; the second half changes
depending on which branch they are in):

Thanks, the `.vhwhy` output is exactly what I needed.

One question decides this: **is Vintage Horizons installed on the server, or only on your
client?**

If it is only on your client, a server pregen cannot help, and that is by design rather
than a bug. This mod builds its picture from chunks the server actually sends you, and a
server never sends chunks outside your view distance, whoever generated them. Distant
terrain therefore fills in as you travel. To get the pregen to reach you, the server needs
this mod installed too, and then `/vhgen` builds a cache the server shares with everyone.

If it *is* installed on the server, then this is a bug I have found and am fixing: the
server offers its list of available sections once, when you join. Anything generated after
that, including a pregen run while you are online, is never offered to you until you
relog. `no-data` in that output means "the client does not know this section exists
anywhere", which fits. Relogging after the pregen finishes should fill it in, and the next
release removes the need to.

Either way, please tell me which one it is, and I will confirm.
