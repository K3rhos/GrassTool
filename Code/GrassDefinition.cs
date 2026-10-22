using System;
using Sandbox;
using Sandbox.Rendering;

namespace RedSnail.GrassTool;

/// <summary>
/// How a grass instance is turned into geometry. Both styles share the entire generation pipeline -
/// the painted density map, the slope limit, the distance thinning and the GPU culling - and differ
/// only in what the vertex shader builds out of each instance.
/// </summary>
public enum GrassStyle
{
	/// <summary>
	/// Procedural blades, bent and tapered on the GPU. Nothing to author, almost no overdraw, and
	/// cheap enough for millions of them - but a blade is all it can ever look like.
	/// </summary>
	Blades,

	/// <summary>
	/// Textured cards crossed through each other, cut out of a sprite sheet. Far more convincing
	/// for anything that is not a lawn, at the cost of real overdraw - which is why cards want a
	/// much shorter <see cref="GrassDefinition.MaxDistance"/> than blades do.
	/// </summary>
	Sprites,
}

/// <summary>
/// Look and behaviour of one grass layer. Shared by every <see cref="GrassRenderer"/> that
/// references it, so a whole world can be retuned from a single asset.
/// </summary>
[AssetType(Name = "Grass Definition", Extension = "grassdef", Category = "Grass")]
public sealed class GrassDefinition : GameResource
{
	/// <summary>
	/// Which geometry each instance becomes. Everything below applies to both styles except the
	/// Sprite group, which is cards only, and Root/Tip Color, which blades only - a card takes its
	/// colour from the sheet.
	/// </summary>
	[Property, Group("Style")]
	public GrassStyle Style { get; set; } = GrassStyle.Blades;

	[Property, Group("Density"), Range(1, 32)]
	public int BladesPerCell { get; set; } = 8;

	/// <summary>Blades stop generating past this distance from the camera.</summary>
	[Property, Group("Density"), Range(500, 60000)]
	public float MaxDistance { get; set; } = 12000.0f;

	/// <summary>Distance at which blade count starts thinning out toward <see cref="MaxDistance"/>.</summary>
	[Property, Group("Density"), Range(0, 60000)]
	public float FadeStart { get; set; } = 4000.0f;

	/// <summary>Minimum ground normal Z. Steeper cells generate nothing, so cliffs stay bare.</summary>
	[Property, Group("Density"), Range(0, 1)]
	public float SlopeLimit { get; set; } = 0.5f;

	/// <summary>
	/// How tall, before the distance thinning scales the last one of a cell down. For a sprite
	/// card this is the height of the card itself.
	/// </summary>
	[Property, Group("Blade")]
	public RangedFloat Height { get; set; } = new(16.0f, 30.0f);

	/// <summary>
	/// How wide. The default is blade-sized: a sprite card wants this close to <see cref="Height"/>
	/// because the sprites are square, and left at a blade's width a card renders as a sliver.
	/// Both come from the same random roll, so a large piece of grass is large in both directions.
	/// </summary>
	[Property, Group("Blade")]
	public RangedFloat Width { get; set; } = new(1.5f, 2.5f);

	/// <summary>How far the blade tip leans forward at rest, as a fraction of its height.</summary>
	[Property, Group("Blade"), Range(0, 1)]
	public float Curve { get; set; } = 0.25f;

	/// <summary>Blends the blade's up axis from world up (0) toward the ground normal (1).</summary>
	[Property, Group("Blade"), Range(0, 1)]
	public float AlignToGround { get; set; } = 0.35f;

	[Property, Group("Color")]
	public Color RootColor { get; set; } = new(0.11f, 0.20f, 0.06f);

	[Property, Group("Color")]
	public Color TipColor { get; set; } = new(0.42f, 0.58f, 0.18f);

	/// <summary>Per-blade brightness jitter, so a field doesn't read as one flat sheet.</summary>
	[Property, Group("Color"), Range(0, 1)]
	public float ColorVariation { get; set; } = 0.25f;

	/// <summary>Darkening at the blade root, faking the occlusion of a dense canopy.</summary>
	[Property, Group("Color"), Range(0, 1)]
	public float RootOcclusion { get; set; } = 0.4f;

	/// <summary>
	/// Light bleeding through the blade from a source behind it. This is most of what sells a
	/// field backlit by a low sun. Fades out toward the root, where the canopy is dense.
	/// </summary>
	[Property, Group("Color"), Range(0, 2)]
	public float Transmission { get; set; } = 0.5f;

	/// <summary>
	/// Softens the lighting terminator. A blade is thin rather than solid, so a hard cutoff at
	/// grazing light turns a field into a mass of black edges.
	/// </summary>
	[Property, Group("Color"), Range(0, 1)]
	public float Wrap { get; set; } = 0.5f;

	/// <summary>
	/// Pushes the lighting normal toward world up. Grass lit by its true geometric normal reads
	/// as a mass of hard black edges; biasing up keeps a field looking like a soft surface.
	/// </summary>
	[Property, Group("Color"), Range(0, 1)]
	public float NormalUpBias { get; set; } = 0.6f;

	[Property, Group("Wind")]
	public Vector2 WindDirection { get; set; } = new(1.0f, 0.35f);

	[Property, Group("Wind"), Range(0, 4)]
	public float WindStrength { get; set; } = 0.35f;

	[Property, Group("Wind"), Range(0, 10)]
	public float WindSpeed { get; set; } = 1.6f;

	/// <summary>Spatial frequency of the travelling wind wave. Smaller values give broader gusts.</summary>
	[Property, Group("Wind")]
	public float WindWaveScale { get; set; } = 0.0016f;

	/// <summary>
	/// Material the cards are drawn with: one using the <c>grass_sprite</c> shader, with the sprite
	/// sheet in its Color slot and a greyscale image of the cutout in its Alpha slot. Both are read
	/// as a grid of <see cref="SpriteColumns"/> x <see cref="SpriteRows"/> cells.
	///
	/// The cutout has to be its own image. Pointing the Alpha slot at an RGBA sheet reads its
	/// luminance rather than its alpha channel, which gives every card a solid rectangle with the
	/// plant cut out of it.
	///
	/// The sheet travels in a material rather than being handed to the shader directly because
	/// that is what the engine's texture streaming watches. A texture passed straight as a render
	/// attribute is invisible to it, and gets its mips evicted out from under the draw.
	/// </summary>
	[Property, Group("Sprite")]
	public Material SpriteMaterial { get; set; }

	[Property, Group("Sprite"), Range(1, 16)]
	public int SpriteColumns { get; set; } = 4;

	[Property, Group("Sprite"), Range(1, 16)]
	public int SpriteRows { get; set; } = 4;

	/// <summary>
	/// How many cells of the sheet actually hold a sprite. They are taken in reading order from the
	/// top left and each card picks one at random, so a part-filled sheet needs this set to the
	/// number of real sprites or cards will draw the empty cells.
	/// </summary>
	[Property, Group("Sprite"), Range(1, 64)]
	public int SpriteCount { get; set; } = 4;

	/// <summary>
	/// Quads each card is built from, spread evenly over a half turn. Two is the classic cross and
	/// is almost always the right answer; three covers more viewing angles for half as much again
	/// in overdraw.
	/// </summary>
	[Property, Group("Sprite"), Range(1, 3)]
	public int Planes { get; set; } = 2;

	/// <summary>
	/// Alpha below which a pixel of the sheet is thrown away. Cards are cut out rather than
	/// blended, which is what lets thousands of them draw in any order - the trade is that this
	/// threshold is a hard edge. Lower it if distant grass thins out, which is mipmapping eroding
	/// the alpha channel.
	/// </summary>
	[Property, Group("Sprite"), Range(0, 1)]
	public float AlphaCutoff { get; set; } = 0.35f;

	/// <summary>
	/// How much of the far end of <see cref="MaxDistance"/> cards fade in over, dithered per pixel.
	/// This is also what hides grass streaming in, since a card entering range does so at the far
	/// edge where it is fully faded out. Worth a good fraction of the render distance.
	/// </summary>
	[Property, Group("Sprite"), Range(0, 4000)]
	public float CardFade { get; set; } = 800.0f;

	/// <summary>Multiplies the sheet, for matching one set of sprites to a drier or greener scene.</summary>
	[Property, Group("Sprite")]
	public Color CardTint { get; set; } = Color.White;

	/// <summary>Sprites the sheet really holds, clamped to the cells the grid actually has.</summary>
	public int SpriteVariantCount => Math.Clamp(SpriteCount, 1, Math.Max(SpriteColumns, 1) * Math.Max(SpriteRows, 1));

	/// <summary>Quads per card, clamped to what the shader's half-turn spread supports.</summary>
	public int PlaneCount => Math.Clamp(Planes, 1, 3);

	public void ApplyTo(CommandList.AttributeAccess attributes)
	{
		attributes.Set("GrassBladesPerCell", BladesPerCell);
		attributes.Set("GrassMaxDistance", MaxDistance);
		attributes.Set("GrassFadeStart", MathX.Clamp(FadeStart, 0.0f, MaxDistance - 1.0f));
		attributes.Set("GrassSlopeLimit", SlopeLimit);

		attributes.Set("GrassBladeHeight", new Vector2(Height.Min, Height.Max));
		attributes.Set("GrassBladeWidth", new Vector2(Width.Min, Width.Max));
		attributes.Set("GrassBladeCurve", Curve);
		attributes.Set("GrassAlignToGround", AlignToGround);

		attributes.Set("GrassRootColor", (Vector3)RootColor);
		attributes.Set("GrassTipColor", (Vector3)TipColor);
		attributes.Set("GrassColorVariation", ColorVariation);
		attributes.Set("GrassRootOcclusion", RootOcclusion);
		attributes.Set("GrassTransmission", Transmission);
		attributes.Set("GrassWrap", Wrap);
		attributes.Set("GrassNormalUpBias", NormalUpBias);

		attributes.Set("GrassWindDirection", WindDirection.Normal);
		attributes.Set("GrassWindStrength", WindStrength);
		attributes.Set("GrassWindSpeed", WindSpeed);
		attributes.Set("GrassWindWaveScale", WindWaveScale);

		// The generation pass picks a sprite for every instance whichever style is drawing. One
		// sprite means they all get variant zero, which is what blades read and ignore.
		attributes.Set("GrassSpriteCount", Style == GrassStyle.Sprites ? SpriteVariantCount : 1);

		if (Style != GrassStyle.Sprites)
			return;

		attributes.Set("GrassCardPlanes", PlaneCount);
		attributes.Set("GrassSpriteColumns", SpriteColumns);
		attributes.Set("GrassSpriteRows", SpriteRows);
		attributes.Set("GrassCardFadeRange", CardFade);
		attributes.Set("GrassAlphaCutoff", AlphaCutoff);
		attributes.Set("GrassCardTint", (Vector3)CardTint);
	}

	protected override Bitmap CreateAssetTypeIcon(int _Width, int _Height)
	{
		return CreateSimpleAssetTypeIcon("grass", _Width, _Height, "#070f0a", "#7cfba9");
	}
}
