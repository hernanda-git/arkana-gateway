namespace Arkana.Gateway.Api.Components;

/// <summary>
/// Standard modal size presets used by <see cref="Modal"/> and <see cref="ConfirmDialog"/>.
/// </summary>
public enum ModalSize
{
    Small,   // 400px
    Default, // 480px
    Wide,    // 640px
    Large,   // 820px (api-key editor: two-column, no vertical scroll)
    Full,    // 92vw / 90vh
    Drawer   // right-side slide-in panel
}
