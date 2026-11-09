"use client";

/**
 * The full product demo (Bunny Stream). On the homepage it opens from "Watch
 * the full demo" in "How it works", and this player only mounts while that
 * dialog is open. `preload=false` stays pinned (#759): `true` downloads
 * ~640 KB of HLS segments and starts a RUM probe before any click.
 */
export default function VideoDemo() {
  return (
    <div className="relative w-full overflow-hidden rounded-2xl pt-[56.25%]">
      <iframe
        allow="accelerometer;gyroscope;autoplay;encrypted-media;picture-in-picture;"
        allowFullScreen={true}
        className="absolute inset-0 h-full w-full border-0"
        loading="lazy"
        src="https://iframe.mediadelivery.net/embed/523175/379cb709-30bd-4155-8f70-8965c9d0430d?autoplay=false&loop=false&muted=false&preload=false&responsive=true"
        title="HyperWhisper demo video"
      />
    </div>
  );
}
