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
