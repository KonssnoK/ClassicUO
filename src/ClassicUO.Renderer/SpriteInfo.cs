using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace ClassicUO.Renderer
{
    public struct SpriteInfo
    {
        public Texture2D Texture;
        public Rectangle UV;
        public Point Center;
        // Logical (pre-upscale) sprite dimensions. For non-HD assets this equals UV.Width/Height.
        // For HD assets this is the original legacy size, used by everything that draws the sprite
        // into world space or measures it for hit-testing.
        public Point LogicalSize;

        public static readonly SpriteInfo Empty = new SpriteInfo { Texture = null };
    }
}
