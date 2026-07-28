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

## Coverage

About 5,400 distinct assets have been prepared and rendered across the equipment
set, plus four whole-corpus scans of all 12,340 equipment PACs. Every weapon and
shield in the archive (735) has been reviewed on its correct camera.
