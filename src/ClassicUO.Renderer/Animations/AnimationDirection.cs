namespace ClassicUO.Renderer.Animations
{
    public struct AnimationDirection
    {
        public uint Address;
        public uint Size;
        public uint Slot;           // AnimIdxBlock index in .idx file (MUL only); used for HD sidecar lookup.
        public byte FrameCount;
        public SpriteInfo[] SpriteInfos;
        public bool IsVerdata;
    }
}
