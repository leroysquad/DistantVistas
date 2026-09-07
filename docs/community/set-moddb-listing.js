async (page) => {
  const html = `<h3>Why this exists</h3>
<p>This was made for me. It was made for <strong>singleplayer</strong>. I wanted something like Minecraft Distant Horizons in Vintage Story. What was on the market was not good enough for how I play. I had the tools, so I forked <strong>Vintage Horizons</strong> (AliasFactory, MIT) and built the version I actually want to look at. Everybody else is along for that if they want to be.</p>
<p>If you can improve it, take it, or change it, I do not care. If the game is better at the end of the day, that is all I care about. Anybody can have the credit. There is no credit due to me.</p>
<p>Why make a mod? Same reason anybody makes anything. There was a void in the marketplace, and I had the skills to fill it. That is it.</p>
<h3>What this is</h3>
<p>Far terrain past vanilla view distance. A persistent level-of-detail cache from chunks your client already has. Real 3D land, trees, and builds you already saw. Not a fog silhouette. I am not trying to replace Horizons. Credit stays with AliasFactory and everyone else who is actually owed it.</p>
<p><strong>What works for me may not work for you.</strong> Different worlds, hardware, and servers behave differently. If Horizons or another LOD mod already does what you need, use that.</p>
<p>I will be updating the comment section on this page.</p>
<h3>Recommended: Client MeshRef Leak Fix</h3>
<p>I run this with <a href="https://mods.vintagestory.at/vsvaogc"><strong>Client MeshRef Leak Fix</strong></a> (<code>vsvaogc</code>). It is client-only. A dedicated server does not need it. Distant Vistas does not require it to load.</p>
<h3>Singleplayer vs client-side</h3>
<ul>
<li><strong>Singleplayer:</strong> install on the client. This is what I built it for and what I test.</li>
<li><strong>Client-side:</strong> it draws from what the client already loaded. You do not need a server install for that. That is the point.</li>
<li><strong>Server assist:</strong> exists if you put it on a server too. That is not an official feature I care about. I did not make this for multiplayer first.</li>
</ul>
<h3>Latest: 1.0 (official)</h3>
<p>This is the official 1.0. Far 3D land that stays. Seasons and winter frost match the ground. Join overlay paints the horizon. Drop <code>distantvistas_1.0.0.zip</code> in Mods. Do not extract. Fully quit Vintage Story, then start it again. Leaving to the menu does not load a new zip.</p>
<ul>
<li><strong>Join overlay.</strong> Four-season Distant Vistas splash. The hop-scan paints a disk of far land so hills exist when you spawn. Graphics view is held at 750 for that pass, then your real slider comes back. It never leaves 750, 1000, or a maxed leftover stuck. Overlay does not freeze Windows. Mouse look and world audio restore when it ends. Esc goes to the menu.</li>
<li><strong>Seasons.</strong> Overlay and walking share one bake from the live month. Summer is bright green. Winter without a snow sheet is mottled brown, tan, and olive (texture specks, not manila plates, not a fake white sheet). Real snow stays snow. Spring and autumn follow the same calendar mix. <code>/time</code> while you are already in the world does not retint. Change month, then rejoin. After 30 days, or a later calendar month, the overlay recaptures.</li>
<li><strong>December frost.</strong> Far pines and oaks frost: green or orange sides, frost-white tops. Frosted grass keeps live colour.</li>
<li><strong>Visited land stays.</strong> Land you already walked does not turn into sky when you leave.</li>
<li><strong>View-distance sky circle.</strong> LOD only steps aside when that tile is actually loaded.</li>
<li><strong>Mountains.</strong> Ridges no longer chop down into caves at the LOD ring.</li>
<li><strong>Spawn plates.</strong> Looking down at spawn no longer paints a low-poly plate over the real ground.</li>
<li><strong>Farseer.</strong> Farseer used to make this mod not render at all. That is fixed. We draw with Farseer in the background. Our tiles sit on top where we have them. Farseer is still fog-colored hills, not far buildings. ChunkLOD and TopoHorizon still make us sit out.</li>
</ul>
<p>Source: <a href="https://github.com/leroysquad/DistantVistas">GitHub</a></p>
<h3>Why a fork, and what is different</h3>
<p>Same starting point as Vintage Horizons: a persistent LOD cache from chunks the client already received. After that the draw path is different.</p>
<ul>
<li><strong>Parent plates.</strong> Horizons will mesh a coarse parent as coverage when children are not ready. From the air that is giant square plates with sheer walls. Distant Vistas does not use a parent box as a stand-in. Incomplete L0 stays off screen until it is real land.</li>
<li><strong>Trail behind the camera.</strong> This build keeps captured land in the draw set behind and beside the camera. Fill is not allowed to stall those tiles just because the window moved.</li>
<li><strong>Vanilla handoff.</strong> Vanilla-owns is 3D plus look-down, not a horizontal disc. Looking down from altitude still draws LOD where vanilla has no chunks.</li>
<li><strong>Night color.</strong> Stored albedo is multiplied by the same live ambient the chunk shaders use.</li>
<li><strong>Vanilla edge fade.</strong> Vanilla chunk shaders fade alpha at the view-distance ring. Those vertex shaders are overridden so LOD joins behind at full alpha instead of a fog slice.</li>
<li><strong>Farseer.</strong> We do not Harmony-patch Farseer. We draw after it. Their heightmap can stay as a cheap backdrop. Ours is the real far terrain.</li>
</ul>
<h3>Credits</h3>
<p>Fork of <strong>Vintage Horizons</strong> (AliasFactory, MIT). Rendering tricks adapted from <strong>Farseer</strong> (Badgerson, MIT). Built and shared by <strong>IllLeroySquad</strong>. Not claiming anyone else's work.</p>
<h3>In-game commands</h3>
<ul>
<li><code>.dvistas</code> status, meshes, far edge, server assist</li>
<li><code>.dvdetail [blocks]</code> detail halving distance (default 512)</li>
<li><code>.dvfar</code> cap LOD distance (<code>0</code> = unlimited)</li>
<li><code>.dvdefer [on|off]</code> defer when ChunkLOD or TopoHorizon draws (restart to apply). Farseer no longer uses this path.</li>
</ul>
<h3>Author testing (1.22.7, RTX 3090)</h3>
<ul>
<li>Singleplayer: high view distance, stable FPS after fill window. This is the instance that looks good.</li>
<li>Multiplayer: LOD works but is limited by server view distance and chunk streaming</li>
</ul>`;

  const summary = 'Official 1.0. Far 3D land that stays. Seasons and winter frost match the ground. Join overlay paints the horizon. Vintage Horizons fork.';
  await page.evaluate(({ html, summary }) => {
    const ed = tinymce.get('editor7125');
    if (!ed) throw new Error('no editor');
    ed.setContent(html);
    ed.save();
    const sum = document.querySelector('input[name="summary"]');
    if (sum) sum.value = summary;
  }, { html, summary });
  return await page.evaluate(() => {
    const c = tinymce.get('editor7125').getContent();
    return {
      has10: c.includes('Latest: 1.0 (official)'),
      hasFrost: c.includes('frost-white tops'),
      hasOverlay: c.includes('Join overlay'),
      summary: document.querySelector('input[name="summary"]')?.value,
      len: c.length
    };
  });
}
