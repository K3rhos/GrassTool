# 🌍Overview

This tool allows you to generate millions of individual grass blades in your scene without tanking the performance, it's fully handled by the GPU.

Usage:

- Create an empty gameobject in your scene with a "GrassRenderer" component attached to it
- Create a grass definition in your assets folder (you can leave everything to default for testing purpose)
- Drag and drop this grass definition into the slot of the GrassRenderer
- Now click in the top left corner of your scene view where the tools list is located, select "Grass"
- Start painting the grass on your terrain and have fun :)

NOTE: Grass blades do not follow the terrain changes (this is intentional this way you can paint the grass on almost everything, not just the terrain) but yeah if you do plan to change the terrain shape in the future, remove the grass first in the area before editing it, or you will be left with grass rendering in the air or under the ground!
# 🎨Styles

A grass definition draws in one of two styles. Both share the whole pipeline — the painted density
map, the slope limit, the distance thinning and the GPU culling — and differ only in what each
instance becomes:

- **Blades** — procedural geometry built on the GPU. Nothing to author, almost no overdraw, and
  cheap enough for millions of them. This is the original behaviour and stays the default.
- **Sprites** — textured cards crossed through each other, cut out of a sprite sheet. Far more
  convincing for anything that is not a lawn, at the cost of real overdraw.

## Setting up sprite grass

1. Set `Style` to `Sprites` on the grass definition
2. Drop a material into `Sprite > Sprite Material`. `Assets/materials/grass_sprites.vmat` is ready
   to go with the included sheets; to use your own, copy it and point the two slots at your images.

   The cutout needs its own greyscale image, separate from the colour one — here that is
   `grass_sprites_rgb.png` and `grass_sprites_alpha.png`. An RGBA image in the Alpha slot is read
   for its luminance rather than its alpha, and every card comes out a solid rectangle with the
   plant cut out of it.

   The sheet also has to travel in a material rather than being handed to the shader from C#.
   Texture streaming works out which mips to keep from what it sees materials using, so a texture
   passed straight as a render attribute is invisible to it and has its mips evicted out from under
   the draw — the cards go soft and slightly dark until something touches the asset again.

   The included sheets are a 4x4 grid of 16 sprites, so set `Sprite Count` to 16.

3. To draw only part of a sheet, list the cells in `Sprite > Sprites` — grid indices counting left
   to right then top to bottom from zero. `3, 4, 5` for one definition and `7, 8, 10` for another
   lets a single sheet serve both, instead of cutting a sheet per definition. Leave it empty and
   the first `Sprite Count` cells are used instead.
3. Give it card-sized numbers, because the defaults are blade-sized:
   - `Blade > Width` close to `Blade > Height` — the sprites are square, and left at a blade's
     width a card renders as a sliver
   - `Density > Max Distance` around 4000, which is roughly 100 m since a unit is an inch. Cards
     cost far more per instance than blades, so the 12000 default is not a good starting point
   - `Density > Blades Per Cell` low — 2 or 3 goes a long way with cards

Cards fade in over the last `Sprite > Card Fade` units of the render distance, dithered per pixel.
That is also what hides grass streaming in as you walk, since a card entering range does so at the
far edge where it is fully faded out. Cards are cut out rather than blended, so thousands of them
draw in any order with no sorting and no blending.

## Upright and ground cards

The sprite shader builds two shapes, chosen by the **Ground Cards** feature on the material rather
than by the grass definition — so a sheet of ground cover can't be paired with the wrong shape:

- **off** — the classic cross: quads standing up out of the ground, crossed through each other.
- **on** — a flat cross: quads lying *in* the ground, for moss and flattened grass. Each plane tips
  the opposite way by `Ground Tilt` degrees so the pair reads as a shallow X instead of one decal,
  and `Ground Lift` raises the patch clear of the surface so it doesn't z-fight. Ground cards are
  square and take both axes from `Blade > Width`; `Height` does nothing for them, and they don't
  sway, since moss on a rock has nothing to bend.

## Layers

A renderer holds a list of **layers**, each one a kind of grass: its own definition, its own
material, and its own painted coverage. Add one per kind — tall grass, ground cover, whatever — and
pick which one the brush writes into from the **Layer** dropdown at the top of the Grass tool's
sidebar. "Clear Layer" only wipes the layer you have selected.

Each layer carries a **Seed**. Every random choice a cell makes — where its grass sits, which way
it faces, how big it is, which sprite it takes — comes from hashing the cell's position, so two
layers over the same ground with the same seed would drop their grass in exactly the same spots. A
new layer picks a random one, so this normally looks after itself; set two layers to the same seed
on purpose and they line up, which is occasionally what you want (flowers that always sit in the
same tufts as the grass).

Because each layer keeps its own coverage, they overlap freely: ground cover can sit *under* tall
grass in the same cells rather than competing for them. Density, render distance, card size and
style are all per-definition too, and ground cover usually wants a shorter distance and tighter
spacing than grass that stands up.

Layers share one instance buffer and draw one after another, so `Max Grass Objects` has to fit the
largest single layer rather than their total — adding a layer costs a dispatch and a draw, not
another allocation of the whole budget.

Only one renderer is allowed per scene — a second one removes itself on awake and says so in the
console, since layers are the way to have more than one kind of grass. A renderer painted before
layers existed migrates into its first layer on load, keeping its definition and everything painted
into it.
# 🔌Hooks

`GrassRenderer.IEvents` lets a game steer the grass from its own code, without changing the library.
Implement it on any component in the same scene as the renderer:

```csharp
public sealed class SnowGrass : Component, GrassRenderer.IEvents
{
	void GrassRenderer.IEvents.OnGrassSize( GrassRenderer renderer, GrassLayer layer, ref float size )
	{
		size *= 1.0f - SnowDepth; // 0..1: the grass sinks into the snow, and is gone at full depth
	}
}
```

- **OnGrassSize** is asked every frame for each layer about to draw. Scale `size` to shrink the
  layer's grass — height and width together, blades and cards alike — or to grow it. It starts at
  1, which draws the grass as painted. At 0 the layer is skipped outright, with no generation pass
  and no draw, so grass that is meant to be gone costs nothing. With several listeners each one gets
  the value the previous one left, so multiply rather than assign.
