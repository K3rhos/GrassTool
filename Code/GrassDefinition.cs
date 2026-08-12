using Sandbox;
using Sandbox.Rendering;

namespace RedSnail.GrassTool;

/// <summary>
/// Look and behaviour of one grass layer. Shared by every <see cref="GrassRenderer"/> that
/// references it, so a whole world can be retuned from a single asset.
/// </summary>
[AssetType( Name = "Grass Definition", Extension = "grassdef", Category = "Grass" )]
public sealed class GrassDefinition : GameResource
{
	[Property, Group( "Density" ), Range( 1, 32 )]
	public int BladesPerCell { get; set; } = 8;

	/// <summary>Blades stop generating past this distance from the camera.</summary>
	[Property, Group( "Density" ), Range( 500, 60000 )]
	public float MaxDistance { get; set; } = 12000.0f;

	/// <summary>Distance at which blade count starts thinning out toward <see cref="MaxDistance"/>.</summary>
	[Property, Group( "Density" ), Range( 0, 60000 )]
	public float FadeStart { get; set; } = 4000.0f;

	/// <summary>Minimum ground normal Z. Steeper cells generate nothing, so cliffs stay bare.</summary>
	[Property, Group( "Density" ), Range( 0, 1 )]
	public float SlopeLimit { get; set; } = 0.5f;

	[Property, Group( "Blade" )]
	public RangedFloat Height { get; set; } = new( 16.0f, 30.0f );

	[Property, Group( "Blade" )]
	public RangedFloat Width { get; set; } = new( 1.5f, 2.5f );

	/// <summary>How far the blade tip leans forward at rest, as a fraction of its height.</summary>
	[Property, Group( "Blade" ), Range( 0, 1 )]
	public float Curve { get; set; } = 0.25f;

	/// <summary>Blends the blade's up axis from world up (0) toward the ground normal (1).</summary>
	[Property, Group( "Blade" ), Range( 0, 1 )]
	public float AlignToGround { get; set; } = 0.35f;

	[Property, Group( "Color" )]
	public Color RootColor { get; set; } = new( 0.11f, 0.20f, 0.06f );

	[Property, Group( "Color" )]
	public Color TipColor { get; set; } = new( 0.42f, 0.58f, 0.18f );

	/// <summary>Per-blade brightness jitter, so a field doesn't read as one flat sheet.</summary>
	[Property, Group( "Color" ), Range( 0, 1 )]
	public float ColorVariation { get; set; } = 0.25f;

	/// <summary>Darkening at the blade root, faking the occlusion of a dense canopy.</summary>
	[Property, Group( "Color" ), Range( 0, 1 )]
	public float RootOcclusion { get; set; } = 0.4f;

	[Property, Group( "Color" ), Range( 0, 1 )]
	public float Roughness { get; set; } = 0.7f;

	/// <summary>
	/// Pushes the lighting normal toward world up. Grass lit by its true geometric normal reads
	/// as a mass of hard black edges; biasing up keeps a field looking like a soft surface.
	/// </summary>
	[Property, Group( "Color" ), Range( 0, 1 )]
	public float NormalUpBias { get; set; } = 0.6f;

	[Property, Group( "Wind" )]
	public Vector2 WindDirection { get; set; } = new( 1.0f, 0.35f );

	[Property, Group( "Wind" ), Range( 0, 4 )]
	public float WindStrength { get; set; } = 0.35f;

	[Property, Group( "Wind" ), Range( 0, 10 )]
	public float WindSpeed { get; set; } = 1.6f;

	/// <summary>Spatial frequency of the travelling wind wave. Smaller values give broader gusts.</summary>
	[Property, Group( "Wind" )]
	public float WindWaveScale { get; set; } = 0.0016f;

	public void ApplyTo( CommandList.AttributeAccess attributes )
	{
		attributes.Set( "GrassBladesPerCell", BladesPerCell );
		attributes.Set( "GrassMaxDistance", MaxDistance );
		attributes.Set( "GrassFadeStart", MathX.Clamp( FadeStart, 0.0f, MaxDistance - 1.0f ) );
		attributes.Set( "GrassSlopeLimit", SlopeLimit );

		attributes.Set( "GrassBladeHeight", new Vector2( Height.Min, Height.Max ) );
		attributes.Set( "GrassBladeWidth", new Vector2( Width.Min, Width.Max ) );
		attributes.Set( "GrassBladeCurve", Curve );
		attributes.Set( "GrassAlignToGround", AlignToGround );

		attributes.Set( "GrassRootColor", (Vector3)RootColor );
		attributes.Set( "GrassTipColor", (Vector3)TipColor );
		attributes.Set( "GrassColorVariation", ColorVariation );
		attributes.Set( "GrassRootOcclusion", RootOcclusion );
		attributes.Set( "GrassRoughness", Roughness );
		attributes.Set( "GrassNormalUpBias", NormalUpBias );

		attributes.Set( "GrassWindDirection", WindDirection.Normal );
		attributes.Set( "GrassWindStrength", WindStrength );
		attributes.Set( "GrassWindSpeed", WindSpeed );
		attributes.Set( "GrassWindWaveScale", WindWaveScale );
	}
	
	protected override Bitmap CreateAssetTypeIcon(int _Width, int _Height)
	{
		return CreateSimpleAssetTypeIcon("grass", _Width, _Height, "#070f0a", "#7cfba9");
	}
}
