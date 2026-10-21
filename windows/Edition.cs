// Which look this build has. GitHub builds both editions from this one folder:
//   default        → the same pink Y2K style as the Mac app, with a wallpaper-coloured widget
//   -p:Edition=XP  → the Windows XP edition (Windows XP or Black & White, chosen in the tray menu)
namespace ScreenshotVault;

static class Edition
{
#if EDITION_XP
    public const bool IsXP = true;
#else
    public const bool IsXP = false;
#endif
}
