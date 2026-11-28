// The Unity 2022.3 macOS export links this C++ entry point from UnityPlayer.dylib.
int PlayerMain(int argc, const char *argv[]);
extern "C" void AvaInstallDesktopHost();

int main(int argc, const char *argv[])
{
    AvaInstallDesktopHost();
    return PlayerMain(argc, argv);
}
