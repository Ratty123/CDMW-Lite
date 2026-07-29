# Preview material audit

Findings from auditing the model preview against the shipped archive. Recorded so
that settled questions are not reopened and refuted explanations are not chased
again.

## How a preview is checked against its source

Two references exist, and they answer different questions.

**The asset's own textures.** Decode the visible base textures a part binds and
compare their mean saturation and value with the render. This is the reference
for *reproduction*: a preview should show an asset at the brightness and colour
its own albedo carries. Over 238 weapons the preview reproduces value at 0.982
and saturation at 0.926.

**The game's item icons.** `ui/texture/icon/itemicon_prefab_<model>.dds` exists
for most equipment, rendered from the same assets by the game's own pipeline, and
is a 256x256 DXT5 whose alpha gives a clean object mask. It is the reference for
*intent* — but it is graded, not raw: measured against the same textures the icons
run **1.174x their source saturation** and **0.986x their value**. Match the value,
never the saturation. Chasing the icons' saturation paints on a look the assets do
not carry.

**Channel layouts differ by shader family.** An `_sp` map does not mean the same
thing everywhere. Equipment pins red at a constant 1.0 and carries roughness in
green, metalness in blue. Hair carries only roughness. Skin uses red for
subsurface and leaves blue saturated, so reading blue as metalness there makes
every face fully metallic. Check what a channel holds for the family before
decoding it.

**Measure texture, greyness and flatness separately.** They have different
causes and conflating them wastes a sweep. Chroma says grey; the light-to-shadow
spread says flat; and whether a part is *textured at all* is high-frequency
energy -- the mean absolute deviation of each pixel from its local
neighbourhood. A bound albedo produces per-texel variation and a flat fill does
not. Over 1,200 assets, 122 measured almost no texture detail and 104 of those
were equally flat in the unlit pass, so the flatness was in the albedo and not
in the renderer.

**Pair every render with an unlit one.** A lit/unlit pair on identical framing
isolates the lighting stage from the albedo pipeline and is the only cheap way to
tell "the source is dark" from "the renderer darkens it". Across 728 weapons and
shields the ratio is 0.97-1.01, so lighting preserves albedo.

**Follow the package camera.** Captures must open an asset on the camera its
preview package declares, which is what the app does. Imposing a fixed angle shows
weapons and shields edge-on at about a sixth of their object pixels, and every
statistic taken from such a capture is wrong — that error alone inflated a measured
brightness deficit from 0.932 to 0.850.

## Confirmed authored — do not "fix" these

Each of these looks like a defect and is not. All were traced to the source.

- **`cd_phm_03_shield_0121`** renders a red thorn ring. Its material authors
  `emissive_color` `[0.435, 0, 0]` over a single-channel intensity mask. Authored
  red emissive.
- **`cd_phm_03_shield_0112`**'s black centre is a `[0.02, 0.02, 0.02]` tint.
- **`cd_phm_01_axe_0003`**'s saturated orange is in its own untinted textures,
  which measure value 0.78-0.86.
- **Neon and pastel garments** — `cd_nhw_00_mask_0010` at texture mean
  `1.00/0.95/0.13`, `cd_phw_00_hand_acc_0023` at `0.19/0.46/0.64`, and others.
  The textures are those colours.
- **Hair, beards and fur** read as near-white mid grey because Crimson authors them
  as greyscale sheets tinted per character at runtime. Alpha-weighted, strands
  measure 0.48-0.51.
- **`blackoil.dds`** is authored flat black with `exact_sidecar` authority.
- **`cd_temp_r_m.dds`**, a placeholder decoding to exactly `(1, 0, 0)`, is bound as
  the colour-blending mask on 314 parts across 208 assets. As a mask that means
  "layer R at 100%", which the game reads the same way; those assets render
  correctly. Rejecting it would make the preview *less* faithful. Placeholders are
  rejected from the emissive and height slots, where they would create an effect
  the part does not have, and only demoted in the normal and specular slots.

## Refuted — do not re-chase

Each of these was a plausible correlation that did not survive testing against the
full population.

- **"Previews are desaturated."** They are, relative to the icons — but the icons
  are graded 1.174x above the assets' own textures. Against the source the preview
  sits at 0.926.
- **"Per-asset hue errors."** Hue is undefined on grey. Stratified by how colourful
  the object actually is, the error is 17.9 degrees below 0.10 saturation and 9.5
  degrees above 0.30. Restricted to genuinely coloured weapons the median is 8.2
  degrees with 9% over 30 — the preview matches the game's reference where hue
  means anything. The signed shift is -2.8 degrees, so there is no colour cast.
- **"The `cd_texturelayer_013` library suppresses colour."** Assets binding a vivid
  tile render at 0.242 median saturation against 0.221 for neutral ones.
- **"The key light is fixed in world space, so overhead-framed weapons are lit
  edge-on."** The camera transforms the model, so the light is effectively a
  headlight.
- **"A colour-blending mask in the material slot is sampled as roughness."** The
  correlation looked strong on seven hand-picked assets. Across 728 with lit/unlit
  pairs, mask-slot assets measure 1.014 and real-surface-map assets 0.974, with the
  same 6-7% dark tail in both.

The last three failed the same way: a hypothesis formed on a handful of assets
selected *because* they were extreme. Test against the full population with a
paired measurement before believing a correlation.

## Corrected

`cd_phm_03_shield_0137` and its `phw` variant were listed above as authored dark,
twice, on the arithmetic of their layer tile and tint. That was wrong. The unlit
render shows the resolved albedo is a mid-toned teal; the renderer was crushing it
to 0.20 of that, and the shield is now readable at 0.84. The mistake was
estimating an albedo from a tile and a tint by hand instead of reading it off the
unlit render — layer compositing produces the actual albedo, and hand arithmetic
over one tile does not predict it. Take the albedo from the unlit pass.

That is also why "roughly 6% of assets being dark is the distribution" was too
comfortable a conclusion. Some of that tail was a defect: 4% of assets rendered
below half the brightness their own albedo carries, and correcting the metal
compensation below halved it.

## Open — diagnosed, not fixed

**A colourless overlay can take a part's albedo from a coloured layer.** An `_o`
overlay is a wear or paint wash laid over a colour, and the ones shipped here are
authored achromatic. Where the overlay is the only candidate with sidecar
authority it eliminates every colour-layer candidate, and the part renders the
wash instead of the material.

Scope, over 3,504 assets: **662 parts take an `_o` overlay as their base, 554 of
those overlays carry no colour, and 527 of those have a coloured layer available**
behind them.

Worked example, `cd_phw_00_ub_00_0161`. Rendering its eleven parts in isolation
identifies p3 as the sleeves, which the game shows as dark leather and the preview
shows as flat pale grey. Its base is `cd_phw_00_ub_00_0145_o.dds`, decoding to RGB
0.489 / 0.492 / 0.489 -- mean chroma 0.003, a pure grey wash. The layer behind it,
`cd_texturelayer_002_0003`, measures 0.108 mean chroma, a real brown. Its torso,
p9 and p10, is separately grey because it draws neutral library tiles with the
authored tint at strength 0.

The entry point is the `layer_diffuse_candidate` skip in
`best_base_binding_for_mode`, which drops every layer candidate once
`availability.authoritative_sidecar` is set.

Three fixes were tried and none fired, so do not repeat them without
instrumenting first:

1. Penalising the overlay's scoring bonus. The overlay was not winning through
   that bonus.
2. Exempting coloured layers from the elimination rule. That only makes them
   eligible; the overlay still outscores them.
3. Both together. The chroma predicate never matched the binding at all.

The untested suspicion is that `inspect_dds_channel_statistics` decodes only BC1
and BC3, so a layer in another format returns invalid and a chroma test silently
never sees it. Confirm that by logging what the selector actually considers
before writing a fourth attempt.

## Coverage

About 6,600 distinct assets have been prepared and rendered, plus four
whole-corpus scans of all 12,340 equipment PACs. Every weapon and shield in the
archive (735) has been reviewed on its correct camera, and a 1,200-asset sweep
spanning equipment, monsters, NPCs and props has been screened for flat, grey and
untextured output with a paired unlit render.
