namespace Asparagus.Sdk.Internal.Tracking
{
    /// <summary>
    /// Shared per-window sequential call counter (<c>1, 2, 3, ...</c>) across game events,
    /// game state changes, and SDK-owned <c>nx10_*</c> state changes.
    /// </summary>
    public sealed class WindowCallOrder
    {
        private uint _order;

        public uint Current
        {
            get { return _order; }
        }

        public uint NextOrder()
        {
            return ++_order;
        }

        public void ResetWindow()
        {
            _order = 0u;
        }
    }
}
