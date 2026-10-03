import PortalSkeleton from "@/components/user/PortalSkeleton";

/** Dashboard fallback (#915): a neutral shape, since regular users land here. */
export default function Loading() {
  return <PortalSkeleton variant="panel" />;
}
