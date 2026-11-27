// Home's visualizer ring, drawn entirely on the graphics card (see AudioRing.cs).
//
// Every pixel works out which bar it's next to from its angle around the centre, reads that bar's level
// from a small strip texture the app updates each frame, and is lit if it falls inside the bar's rounded
// capsule. The soft ring also draws a smooth thick line around the bars' tips, with a faint haze, by
// reading the (already smoothed) level at its exact angle.
//
// Two passes, as two elements (one shader model 2 pixel shader can't hold both):
//   fxc /T ps_2_0 /E Bars /O3 /Fo AudioRingBars.ps AudioRing.fx
//   fxc /T ps_2_0 /E Line /O3 /Fo AudioRingLine.ps AudioRing.fx

sampler2D input : register(s0);   // the element itself (transparent, unused)
sampler2D levels : register(s1);  // one row: level by angle (-0.5..0.5 turns), 16-bit split over red (high) and green (low)

float4 geo : register(c0);  // width, height (px), inner radius, reach (px a full bar adds)
float4 bar : register(c1);  // bar half width, shortest bar, bar alpha at silence, extra alpha at full
float4 ol : register(c2);   // outline gap past the tips, line half width, line alpha, haze half width
float4 ex : register(c3);   // haze alpha, thin circle radius (0 = none), its alpha, bar count
float4 tex : register(c4);  // strip lookup: x = angle * scale + offset

static const float TwoPi = 6.2831853;

// Angle of p around the centre in turns: 0 at the top, clockwise, -0.5..0.5. A short polynomial instead of
// atan2, which alone would take a third of what this shader model allows (error under 0.0002 of a turn).
float Turns(float2 p)
{
    float2 q = abs(p);
    float lo = min(q.x, q.y), hi = max(q.x, q.y);
    float z = lo / max(hi, 1e-4);
    float s = z * z;
    float a = (((-0.0464964749 * s + 0.15931422) * s - 0.327622764) * s * z + z) / TwoPi; // atan(z), in turns
    a = q.x > q.y ? 0.25 - a : a;  // measured from the vertical
    a = p.y > 0 ? 0.5 - a : a;     // lower half
    return p.x < 0 ? -a : a;       // left side
}

float Level(float t)
{
    float4 s = tex2D(levels, float2(t * tex.x + tex.y, 0.5));
    return s.r + s.g / 255.0;
}

// The bars: the nearest one to this pixel is a capsule from the inner radius out to its level.
float4 Bars(float2 uv : TEXCOORD) : COLOR
{
    float2 p = (uv - 0.5) * geo.xy;
    float r = length(p);
    float t = Turns(p);
    float n = ex.w;
    float tc = (floor(t * n) + 0.5) / n;
    float v = r * (t - tc) * TwoPi;
    float level = Level(tc);
    float tip = geo.z + bar.y + level * geo.w;
    float u = r - clamp(r, geo.z, tip);
    float a = saturate(0.5 + bar.x - sqrt(u * u + v * v)) * (bar.z + bar.w * level);
    // The faint thin circle inside the small ring.
    a = saturate(a + saturate(1.1 - abs(r - ex.y)) * ex.z);
    return float4(a, a, a, a); // white, premultiplied
}

// The smooth thick line around the bars' tips, with a faint haze.
float4 Line(float2 uv : TEXCOORD) : COLOR
{
    float2 p = (uv - 0.5) * geo.xy;
    float r = length(p);
    float edge = abs(r - (geo.z + bar.y + Level(Turns(p)) * geo.w + ol.x));
    float haze = saturate(1 - edge / ol.w);
    float a = saturate(saturate(0.5 + ol.y - edge) * ol.z + haze * haze * ex.x);
    return float4(a, a, a, a);
}
