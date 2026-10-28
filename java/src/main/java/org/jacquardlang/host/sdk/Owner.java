package org.jacquardlang.host.sdk;

import java.io.IOException;
import java.nio.file.Files;
import java.nio.file.LinkOption;
import java.nio.file.Path;
import java.nio.file.attribute.PosixFileAttributeView;
import java.nio.file.attribute.PosixFileAttributes;
import java.nio.file.attribute.PosixFilePermission;
import java.nio.file.attribute.PosixFilePermissions;
import java.nio.file.attribute.UserPrincipal;
import java.util.EnumSet;
import java.util.Set;

/**
 * The cache's trust boundary. Nothing inside a cache entry can resist someone
 * who may rewrite it, so the guarantee is that only the cache's owner can
 * write it: on a POSIX file system, a cache root or entry that this user does
 * not own, or that its group or others may write, is refused, and each new
 * entry's directories and files are made owner-only. Where the file system
 * has no POSIX permissions (Windows) nothing is checked.
 */
final class Owner {
    private Owner() {}

    /** Owner-only permissions for a new directory. */
    static final Set<PosixFilePermission> DIRECTORY = PosixFilePermissions.fromString("rwx------");
    /** Owner-only permissions for a new file. */
    static final Set<PosixFilePermission> FILE = PosixFilePermissions.fromString("rw-------");

    private static final Set<PosixFilePermission> OTHERS = EnumSet.of(PosixFilePermission.GROUP_READ, PosixFilePermission.GROUP_WRITE,
            PosixFilePermission.GROUP_EXECUTE, PosixFilePermission.OTHERS_READ, PosixFilePermission.OTHERS_WRITE,
            PosixFilePermission.OTHERS_EXECUTE);

    /** Whether the default file system has POSIX permissions. */
    static boolean posix() {
        return java.nio.file.FileSystems.getDefault().supportedFileAttributeViews().contains("posix");
    }

    /**
     * Applies {@code access} to {@code path}, which is not followed if it is
     * a link: refuses it unless this user owns it and neither its group nor
     * others may write it, or removes every group and other permission.
     */
    static void check(Path path, Core.Access access) throws JacquardException {
        if (access == Core.Access.ANY || !posix()) {
            return;
        }
        PosixFileAttributes attributes;
        try {
            attributes = Files.getFileAttributeView(path, PosixFileAttributeView.class, LinkOption.NOFOLLOW_LINKS).readAttributes();
        } catch (IOException | RuntimeException error) {
            throw JacquardException.preparation("cannot read " + path + ": " + error.getMessage());
        }
        if (access == Core.Access.RESTRICT) {
            Set<PosixFilePermission> kept = EnumSet.noneOf(PosixFilePermission.class);
            kept.addAll(attributes.permissions());
            if (kept.removeAll(OTHERS)) {
                try {
                    Files.setPosixFilePermissions(path, kept);
                } catch (IOException | RuntimeException error) {
                    throw JacquardException.preparation("cannot make " + path + " owner-only: " + error.getMessage());
                }
            }
            return;
        }
        if (!isCurrentUser(path, attributes.owner())) {
            throw JacquardException.preparation("cache path " + path + " is not owned by the current user; only its owner may write the cache");
        }
        Set<PosixFilePermission> permissions = attributes.permissions();
        if (permissions.contains(PosixFilePermission.GROUP_WRITE) || permissions.contains(PosixFilePermission.OTHERS_WRITE)) {
            throw JacquardException.preparation("cache path " + path + " is group- or world-writable; only its owner may write the cache");
        }
    }

    /**
     * Whether {@code owner} is this process's user: by uid where
     * {@code /proc/self} gives it (Linux), otherwise by the account the JDK
     * reports for this process.
     */
    private static boolean isCurrentUser(Path path, UserPrincipal owner) throws JacquardException {
        try {
            Path self = Path.of("/proc/self");
            if (Files.isDirectory(self)) {
                return Files.getAttribute(self, "unix:uid").equals(Files.getAttribute(path, "unix:uid", LinkOption.NOFOLLOW_LINKS));
            }
            UserPrincipal current = path.getFileSystem().getUserPrincipalLookupService()
                    .lookupPrincipalByName(System.getProperty("user.name"));
            return current.equals(owner);
        } catch (IOException | RuntimeException error) {
            throw JacquardException.preparation("cannot tell who owns " + path + ": " + error.getMessage());
        }
    }
}
