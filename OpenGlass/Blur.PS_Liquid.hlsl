// Liquid Glass (iOS 26 style) compose pass for the Direct3D renderer.
// Same inputs as Blur.PS_BlurV (quarter-width horizontally blurred backdrop + 8 vertical taps),
// plus a rounded-rectangle glass shape. Only the curved bezel band bends light: the flat interior
// passes the backdrop through unchanged, like a real slab of glass with rounded edges.
#include "Blur.Common.hlsli"

cbuffer LiquidConstants : register(b1)
{
	float4 glassRect;    // left, top, right, bottom in back-buffer pixels
	float4 liquidShape;  // corner radius, bezel width, refraction (px), chromatic dispersion (0..1)
	float4 liquidLight;  // specular strength, 1/width, 1/height, direction (+1 outward, -1 inward)
	float4 liquidMode;   // x: 1 = Aero colorization, 0 = Blur colorization; y: body blur mix; z: rim blur mix; w: frost radius px
	float4 liquidExtra;  // x: body dim (0..1), y: debug view (1 raw backdrop, 2 magenta)
};

// Full-resolution copy of the backdrop, used for the clear (unblurred) part of the glass.
Texture2D<float4> sharpTexture : register(t1);

float SdRoundRect(float2 p)
{
	float2 c = (glassRect.xy + glassRect.zw) * 0.5f;
	float2 b = (glassRect.zw - glassRect.xy) * 0.5f;
	float r = min(liquidShape.x, min(b.x, b.y));
	float2 q = abs(p - c) - b + r;
	return length(max(q, 0.0f)) + min(max(q.x, q.y), 0.0f) - r;
}

// Vertical blur of the quarter-width buffer, with every tap shifted by the same uv offset.
float4 BlurAt(PS_INPUT input, float2 duv)
{
	float4 s = 0;
	s += conv[0].x * texture0.Sample(sampler0, input.tex1.xy + duv);
	s += conv[1].x * texture0.Sample(sampler0, input.tex1.zw + duv);
	s += conv[2].x * texture0.Sample(sampler0, input.tex2.xy + duv);
	s += conv[3].x * texture0.Sample(sampler0, input.tex2.zw + duv);
	s += conv[4].x * texture0.Sample(sampler0, input.tex3.xy + duv);
	s += conv[5].x * texture0.Sample(sampler0, input.tex3.zw + duv);
	s += conv[6].x * texture0.Sample(sampler0, input.tex4.xy + duv);
	s += conv[7].x * texture0.Sample(sampler0, input.tex4.zw + duv);
	return s;
}

// Wider frost: average the stock 8-tap blur at five points of a cross.
float4 FrostAt(PS_INPUT input, float2 duv, float2 step)
{
	return (BlurAt(input, duv) * 2.0f +
		BlurAt(input, duv + float2(step.x, 0.0f)) + BlurAt(input, duv - float2(step.x, 0.0f)) +
		BlurAt(input, duv + float2(0.0f, step.y)) + BlurAt(input, duv - float2(0.0f, step.y))) / 6.0f;
}

float4 main(PS_INPUT input) : SV_Target0
{
	float2 p = input.pos.xy;
	float bezel = max(liquidShape.y, 1.0f);
	float refraction = liquidShape.z;
	float chroma = liquidShape.w;
	float specular = liquidLight.x;
	float2 invSize = liquidLight.yz;
	float direction = liquidLight.w;

	float d = SdRoundRect(p);
	float2 e = float2(1.0f, 0.0f);
	float2 n = float2(SdRoundRect(p + e.xy) - SdRoundRect(p - e.xy), SdRoundRect(p + e.yx) - SdRoundRect(p - e.yx));
	n = n / max(length(n), 1e-4f);   // outward normal of the glass edge

	float edge = 1.0f - saturate(-d / bezel);           // 1 at the rim, 0 on the flat interior
	float bend = refraction * pow(edge, 2.2f);           // squircle-like bezel profile
	float2 offsetPx = n * bend * direction;

	// iOS-style: the bezel stays clear (refraction + dispersion visible), the body is frosted.
	float blurMix = saturate(lerp(liquidMode.y, liquidMode.z, saturate(edge * 1.5f)));
	float2 frostStep = liquidMode.w * invSize;
	float4 blurred = 0;
	if (blurMix > 0.001f)
	{
		blurred = liquidMode.w > 0.5f ? FrostAt(input, offsetPx * invSize, frostStep) : BlurAt(input, offsetPx * invSize);
	}
	float4 sharp = 0;
	if (blurMix < 0.999f)
	{
		float4 g = sharpTexture.Sample(sampler0, (p + offsetPx) * invSize);
		if (bend > 0.01f)
		{
			float r = sharpTexture.Sample(sampler0, (p + offsetPx * (1.0f + chroma)) * invSize).r;
			float b = sharpTexture.Sample(sampler0, (p + offsetPx * (1.0f - chroma)) * invSize).b;
			sharp = float4(r, g.g, b, g.a);
		}
		else
		{
			sharp = g;
		}
	}
	float4 background = lerp(sharp, blurred, blurMix);
	if (liquidExtra.y > 1.5f)
	{
		return float4(1.0f, 0.0f, 1.0f, 1.0f);
	}
	if (liquidExtra.y > 0.5f)
	{
		return float4(background.rgb, 1.0f);
	}

	// Same colorization as the stock compose passes (Blur_V for Aero, Blur_V_Vista for Blur),
	// so existing color/opacity settings still apply.
	float coverage;
	float3 result;
	float alpha;
	if (liquidMode.x > 0.5f)
	{
		coverage = saturate(background.a);
		float luminance = dot(background.rgb, float3(0.2126f, 0.7152f, 0.0722f));
		result = blurBalance.xxx * background.rgb;
		result = luminance * afterglow.xyz + result;
		result = coverage * color.xyz + result;
		result += (1.0f - coverage) * fallback.rgb;
		alpha = coverage + (1.0f - coverage) * fallback.a;
	}
	else
	{
		coverage = 1.0f;
		result = color.xyz + (1.0f - color.a) * background.rgb;
		alpha = color.a + (1.0f - color.a) * background.a;
	}

	// Darken the frosted body a little so text on top stays legible; the rim keeps full brightness.
	result *= 1.0f - liquidExtra.x * (1.0f - edge) * blurMix;

	// Specular rim: light from the top-left, a brighter edge facing it and a softer one opposite.
	float2 L = normalize(float2(-0.45f, -1.0f));
	float facing = saturate(dot(n, L));
	float back = saturate(dot(-n, L));
	float rim = pow(saturate(1.0f - (-d) / 1.6f), 2.0f);
	float highlight = facing * facing * pow(edge, 5.0f) + back * back * pow(edge, 7.0f) * 0.45f + rim * 0.35f;
	result += highlight * specular * coverage;

	return float4(saturate(result), alpha);
}
