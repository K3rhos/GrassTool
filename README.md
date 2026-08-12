# 🌍Overview

This tool allows you to generate millions of individual grass blades in your scene without tanking the performance, it's fully handled by the GPU.

Usage:

- Create an empty gameobject in your scene with a "GrassRenderer" component attached to it
- Create a grass definition in your assets folder (you can leave everything to default for testing purpose)
- Drag and drop this grass definition into the slot of the GrassRenderer
- Now click in the top left corner of your scene view where the tools list is located, select "Grass"
- Start painting the grass on your terrain and have fun :)

NOTE: Grass blades do not follow the terrain changes (this is intentional this way you can paint the grass on almost everything, not just the terrain) but yeah if you do plan to change the terrain shape in the future, remove the grass first in the area before editing it, or you will be left with grass rendering in the air or under the ground!