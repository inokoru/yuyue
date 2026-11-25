package app.yuyue.design

import android.graphics.RenderEffect
import android.graphics.RuntimeShader
import android.os.Build
import androidx.annotation.RequiresApi
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxScope
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.shadow
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.Shape
import androidx.compose.ui.graphics.asComposeRenderEffect
import androidx.compose.ui.graphics.graphicsLayer
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.unit.Dp
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import dev.chrisbanes.haze.HazeInput
import dev.chrisbanes.haze.HazeState
import dev.chrisbanes.haze.blur.HazeBlurStyle
import dev.chrisbanes.haze.blur.HazeColorEffect
import dev.chrisbanes.haze.blur.hazeBlur

/**
 * 液态玻璃效果 on Android, mirroring Apple's `LiquidGlass.swift` (`appearance.liquidGlass`).
 * Uses native AGSL `RuntimeShader` (Android 13 / API 33+) layered over Haze backdrop blur
 * to render edge lens refraction, subtle chromatic dispersion, and specular Fresnel rims on
 * pure-Compose floating chrome (bottom tab bar & now-playing bar). Never used over WebView.
 */
object LiquidGlass {
    const val KEY = "appearance.liquidGlass"

    val supported: Boolean
        get() = Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU
}

// AGSL rounded-capsule lens refraction + Fresnel specular rim shader (Android 13+ / API 33+).
private const val LIQUID_GLASS_AGSL = """
uniform shader content;
uniform float2 size;
uniform float cornerRadius;
uniform float isDark;

float roundedBoxSDF(float2 p, float2 b, float r) {
    float2 q = abs(p) - b + float2(r);
    return length(max(q, 0.0)) + min(max(q.x, q.y), 0.0) - r;
}

half4 main(float2 fragCoord) {
    float2 halfSize = max(size * 0.5, float2(1.0));
    float r = min(cornerRadius, min(halfSize.x, halfSize.y));
    float2 p = fragCoord - halfSize;
    float d = roundedBoxSDF(p, halfSize, r);

    // Distance inward from the capsule rim (0 at edge, >0 inside).
    float rimDist = max(-d, 0.0);
    float bevel = min(26.0, min(halfSize.x, halfSize.y) * 0.65);
    float rimT = 1.0 - clamp(rimDist / max(bevel, 1.0), 0.0, 1.0);
    float lensCurve = rimT * rimT * (3.0 - 2.0 * rimT);

    // Outward normal estimate toward capsule rim for physical edge refraction.
    float2 dir = normalize(p + float2(0.0001));
    float refractPx = lensCurve * 7.5;
    float dispersion = lensCurve * 1.35;

    float2 uvG = clamp(fragCoord - dir * refractPx, float2(0.0), size - float2(1.0));
    float2 uvR = clamp(fragCoord - dir * (refractPx + dispersion), float2(0.0), size - float2(1.0));
    float2 uvB = clamp(fragCoord - dir * max(0.0, refractPx - dispersion), float2(0.0), size - float2(1.0));

    half4 colR = content.eval(uvR);
    half4 colG = content.eval(uvG);
    half4 colB = content.eval(uvB);
    half3 rgb = half3(colR.r, colG.g, colB.b);

    // Directional Fresnel specular sheen (top-left bright rim + soft bottom-right bounce).
    float2 n = normalize(p / max(halfSize, float2(1.0)) + float2(0.0001));
    float topLeftLight = clamp(dot(n, float2(-0.62, -0.78)), 0.0, 1.0);
    float bottomRightBounce = clamp(dot(n, float2(0.55, 0.83)), 0.0, 1.0);
    float edgeHighlight = pow(rimT, 2.2) * (0.34 * topLeftLight + 0.14 * bottomRightBounce);
    float specular = isDark > 0.5 ? (edgeHighlight * 0.65) : edgeHighlight;

    // Subtle luminous glass lift inside the lens.
    half3 lift = isDark > 0.5 ? half3(0.045) : half3(0.075);
    rgb = clamp(rgb + lift * half(lensCurve) + half3(specular), half3(0.0), half3(1.0));
    return half4(rgb, colG.a);
}
"""

@RequiresApi(Build.VERSION_CODES.TIRAMISU)
private fun buildLiquidGlassRenderEffect(size: IntSize, radiusPx: Float, dark: Boolean): RenderEffect? {
    if (size.width <= 0 || size.height <= 0) return null
    val shader = RuntimeShader(LIQUID_GLASS_AGSL)
    shader.setFloatUniform("size", size.width.toFloat(), size.height.toFloat())
    shader.setFloatUniform("cornerRadius", radiusPx)
    shader.setFloatUniform("isDark", if (dark) 1f else 0f)
    return RenderEffect.createRuntimeShaderEffect(shader, "content")
}

/**
 * Floating Liquid Glass container backed by Haze blur + AGSL edge refraction (API 33+).
 * Renders the refracted backdrop layer separately from [content] so icons and text stay razor-sharp.
 */
@Composable
fun LiquidGlassSurface(
    haze: HazeState,
    shape: Shape = RoundedCornerShape(999.dp),
    cornerRadius: Dp = 28.dp,
    modifier: Modifier = Modifier,
    content: @Composable BoxScope.() -> Unit,
) {
    val dark = YY.dark
    val paper = YY.c.bg
    val density = LocalDensity.current
    var boxSize by remember { mutableStateOf(IntSize.Zero) }
    val blurStyle = remember(paper, dark) {
        HazeBlurStyle {
            blurRadius(if (dark) 22.dp else 18.dp)
            backgroundColor(paper)
            colorEffects(
                listOf(
                    HazeColorEffect.tint(
                        if (dark) Color(0xFF232428).copy(alpha = 0.56f)
                        else Color.White.copy(alpha = 0.54f),
                    ),
                ),
            )
        }
    }
    val rimBrush = remember(dark) {
        Brush.linearGradient(
            colors = if (dark) {
                listOf(
                    Color.White.copy(alpha = 0.28f),
                    Color.White.copy(alpha = 0.08f),
                    Color.White.copy(alpha = 0.16f),
                )
            } else {
                listOf(
                    Color.White.copy(alpha = 0.82f),
                    Color.White.copy(alpha = 0.22f),
                    Color.White.copy(alpha = 0.52f),
                )
            },
            start = Offset.Zero,
            end = Offset(Float.POSITIVE_INFINITY, Float.POSITIVE_INFINITY),
        )
    }

    Box(
        modifier
            .onSizeChanged { boxSize = it }
            .shadow(
                elevation = if (dark) 14.dp else 12.dp,
                shape = shape,
                ambientColor = Color.Black.copy(alpha = if (dark) 0.45f else 0.14f),
                spotColor = Color.Black.copy(alpha = if (dark) 0.55f else 0.18f),
            )
            .clip(shape),
    ) {
        // Backdrop layer: Haze real-time blur + AGSL lens refraction (only the glass refracts, not labels).
        val shaderModifier = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU && boxSize.width > 0 && boxSize.height > 0) {
            val radiusPx = with(density) { cornerRadius.toPx() }
            val fx = remember(boxSize, radiusPx, dark) {
                buildLiquidGlassRenderEffect(boxSize, radiusPx, dark)?.asComposeRenderEffect()
            }
            if (fx != null) Modifier.graphicsLayer { renderEffect = fx } else Modifier
        } else {
            Modifier
        }
        Box(
            Modifier
                .matchParentSize()
                .then(shaderModifier)
                .hazeBlur(HazeInput.Sources(haze), blurStyle)
                .background(
                    Brush.verticalGradient(
                        colors = if (dark) {
                            listOf(Color.White.copy(alpha = 0.08f), Color.Transparent)
                        } else {
                            listOf(Color.White.copy(alpha = 0.34f), Color.White.copy(alpha = 0.06f))
                        },
                    ),
                )
                .border(0.75.dp, rimBrush, shape),
        )
        content()
    }
}
