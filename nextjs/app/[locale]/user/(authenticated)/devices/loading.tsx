import PortalSkeleton from "@/components/user/PortalSkeleton";

/** Admin page fallback (#915). The admin check runs in `layout.tsx`, outside it. */
export default function Loading() {
  return <PortalSkeleton variant="table" />;
}
