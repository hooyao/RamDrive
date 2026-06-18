/*
 * winfsp-setsecurity-owner-repro.c
 *
 * Minimal, self-contained reproducer for: a non-privileged caller can reassign the
 * owner of a file/directory via SetSecurity on a WinFsp in-memory file system (e.g.
 * the bundled memfs), whereas real NTFS refuses it with ERROR_INVALID_OWNER (1307).
 *
 * What it does, on each <root> you pass:
 *   1. Creates a fresh directory  <root>\winfsp_owner_repro_<pid>
 *   2. Reads and prints its current owner SID (the creating principal).
 *   3. Picks a *different* well-known owner SID (Administrators, or LocalSystem if the
 *      caller already owns as Administrators) so the attempt is a genuine owner CHANGE.
 *   4. Opens a handle with WRITE_OWNER and calls SetKernelObjectSecurity with
 *      OWNER_SECURITY_INFORMATION only.
 *   5. Reports whether the owner change SUCCEEDED, the resulting owner, and whether the
 *      directory is still deletable by the (same, non-elevated) caller.
 *
 * Run it twice -- once with a WinFsp memfs mount, once with a path on a real NTFS volume --
 * and compare. Run from a NON-ELEVATED shell (that is the whole point).
 *
 * Build (MSVC):   cl /W4 /nologo winfsp-setsecurity-owner-repro.c advapi32.lib
 * Build (MinGW):  gcc -O2 -Wall -o repro winfsp-setsecurity-owner-repro.c -ladvapi32
 *
 * Usage:          repro.exe <root1> [<root2> ...]
 * Example:        repro.exe M:\        C:\Temp
 *                 (M: = memfs mount,  C:\Temp = NTFS control)
 *
 * Expected output: on NTFS the owner change FAILS with win32=1307 (ERROR_INVALID_OWNER)
 * and the directory stays deletable; on memfs the owner change SUCCEEDS and the directory
 * may become undeletable by its creator.
 */

#include <windows.h>
#include <sddl.h>
#include <stdio.h>

static void print_owner(const char *label, const char *path)
{
    UCHAR buf[1024];
    DWORD needed = 0;

    if (!GetFileSecurityA(path, OWNER_SECURITY_INFORMATION, buf, sizeof(buf), &needed))
    {
        printf("    %-18s <GetFileSecurity failed, win32=%lu>\n", label, GetLastError());
        return;
    }

    PSID owner = NULL;
    BOOL defaulted = FALSE;
    if (!GetSecurityDescriptorOwner((PSECURITY_DESCRIPTOR)buf, &owner, &defaulted) || owner == NULL)
    {
        printf("    %-18s <no owner in SD>\n", label);
        return;
    }

    LPSTR sidstr = NULL;
    if (ConvertSidToStringSidA(owner, &sidstr))
    {
        printf("    %-18s %s\n", label, sidstr);
        LocalFree(sidstr);
    }
    else
    {
        printf("    %-18s <ConvertSidToStringSid failed, win32=%lu>\n", label, GetLastError());
    }
}

/* Read the current owner SID string into out (caller frees with LocalFree). NULL on failure. */
static LPSTR get_owner_sid(const char *path)
{
    UCHAR buf[1024];
    DWORD needed = 0;
    if (!GetFileSecurityA(path, OWNER_SECURITY_INFORMATION, buf, sizeof(buf), &needed))
        return NULL;
    PSID owner = NULL;
    BOOL defaulted = FALSE;
    if (!GetSecurityDescriptorOwner((PSECURITY_DESCRIPTOR)buf, &owner, &defaulted) || owner == NULL)
        return NULL;
    LPSTR sidstr = NULL;
    if (!ConvertSidToStringSidA(owner, &sidstr))
        return NULL;
    return sidstr; /* LocalFree by caller */
}

static void run_on_root(const char *root)
{
    char dir[MAX_PATH];
    DWORD pid = GetCurrentProcessId();

    /* Build "<root>\winfsp_owner_repro_<pid>", tolerating a trailing backslash on root. */
    size_t n = strlen(root);
    if (n > 0 && (root[n - 1] == '\\' || root[n - 1] == '/'))
        snprintf(dir, sizeof(dir), "%swinfsp_owner_repro_%lu", root, pid);
    else
        snprintf(dir, sizeof(dir), "%s\\winfsp_owner_repro_%lu", root, pid);

    printf("==== root: %s ====\n", root);
    printf("  target dir: %s\n", dir);

    if (!CreateDirectoryA(dir, NULL))
    {
        printf("  CreateDirectory failed, win32=%lu (skipping this root)\n\n", GetLastError());
        return;
    }

    /* Current owner = creating principal. */
    print_owner("owner before:", dir);
    LPSTR ownerBefore = get_owner_sid(dir);

    /* Pick a DIFFERENT well-known owner so this is a genuine change regardless of who we are.
     * S-1-5-32-544 = BUILTIN\Administrators, S-1-5-18 = LocalSystem (SY). */
    const char *targetSid = "S-1-5-32-544";
    if (ownerBefore && _stricmp(ownerBefore, "S-1-5-32-544") == 0)
        targetSid = "S-1-5-18";

    char sddl[64];
    snprintf(sddl, sizeof(sddl), "O:%s", targetSid);

    PSECURITY_DESCRIPTOR sd = NULL;
    ULONG sdlen = 0;
    if (!ConvertStringSecurityDescriptorToSecurityDescriptorA(sddl, SDDL_REVISION_1, &sd, &sdlen))
    {
        printf("  ConvertStringSD(%s) failed, win32=%lu\n\n", sddl, GetLastError());
        if (ownerBefore) LocalFree(ownerBefore);
        RemoveDirectoryA(dir);
        return;
    }
    printf("  attempting owner change -> %s\n", targetSid);

    /* Open with WRITE_OWNER. FILE_FLAG_BACKUP_SEMANTICS is required to get a directory handle. */
    HANDLE h = CreateFileA(dir, WRITE_OWNER | WRITE_DAC | READ_CONTROL,
                           FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, NULL,
                           OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS, NULL);
    if (h == INVALID_HANDLE_VALUE)
    {
        /* On real NTFS a non-privileged caller may not even get WRITE_OWNER -> ACCESS_DENIED here,
         * which is itself a valid "refused" outcome. */
        printf("  open(WRITE_OWNER) FAILED win32=%lu  => owner change refused at open (NTFS-like)\n",
               GetLastError());
    }
    else
    {
        BOOL ok = SetKernelObjectSecurity(h, OWNER_SECURITY_INFORMATION, sd);
        DWORD err = GetLastError();
        CloseHandle(h);
        if (ok)
            printf("  SetKernelObjectSecurity(OWNER) SUCCEEDED  <== owner reassigned\n");
        else
            printf("  SetKernelObjectSecurity(OWNER) FAILED win32=%lu%s\n", err,
                   err == ERROR_INVALID_OWNER ? " (ERROR_INVALID_OWNER) <== NTFS-like refusal" : "");
    }

    /* Resulting owner + can we still delete it? */
    print_owner("owner after:", dir);

    if (RemoveDirectoryA(dir))
        printf("  RemoveDirectory: OK (still deletable by creator)\n");
    else
        printf("  RemoveDirectory: FAILED win32=%lu (LEAK: creator can no longer delete it)\n",
               GetLastError());

    LocalFree(sd);
    if (ownerBefore) LocalFree(ownerBefore);
    printf("\n");
}

int main(int argc, char **argv)
{
    if (argc < 2)
    {
        fprintf(stderr,
            "usage: %s <root1> [<root2> ...]\n"
            "  e.g. %s M:\\  C:\\Temp     (M: = winfsp memfs mount, C:\\Temp = NTFS control)\n"
            "  run from a NON-ELEVATED shell.\n",
            argv[0], argv[0]);
        return 2;
    }

    BOOL elevated = FALSE;
    HANDLE tok = NULL;
    if (OpenProcessToken(GetCurrentProcess(), TOKEN_QUERY, &tok))
    {
        TOKEN_ELEVATION el;
        DWORD cb = 0;
        if (GetTokenInformation(tok, TokenElevation, &el, sizeof(el), &cb))
            elevated = el.TokenIsElevated;
        CloseHandle(tok);
    }
    printf("running %s\n\n", elevated ? "ELEVATED (note: results are only meaningful non-elevated)"
                                      : "non-elevated");

    for (int i = 1; i < argc; i++)
        run_on_root(argv[i]);

    return 0;
}
