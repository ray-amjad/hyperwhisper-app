import { notFound } from "next/navigation";

import LatencyMatrix from "@/components/latency/LatencyMatrix";
import { getAllLatencyMatrices } from "@/src/content/latency";
import { DEFAULT_BUCKET, WINDOW_DAYS } from "@/lib/latency/types";

export const dynamic = "force-static";
export const revalidate = 3600;

const TITLE = "Speech-to-text latency, measured";
const DESCRIPTION =
  "How fast each speech-to-text provider actually answers HyperWhisper Cloud, by region, over the last 30 days.";

export async function generateMetadata() {
  return {
    title: TITLE,
    description: DESCRIPTION,
    alternates: {
      canonical: "https://hyperwhisper.com/en/latency",
    },
  };
}

type Props = {
  params: Promise<{ locale: string }>;
};

/**
 * Public latency page. English only, like the blog — the numbers are the point
 * and translating 40 copies of them buys nothing.
 *
 * Static with an hourly revalidate: the aggregate is a 30-day window, so a page
 * up to an hour old tells the same story as a fresh one, and every visitor gets
 * a cached response.
 */
export default async function LatencyPage({ params }: Props) {
  const { locale } = await params;
  if (locale !== "en") {
    notFound();
  }

  const matrices = await getAllLatencyMatrices();
  const totalSamples = Object.values(matrices).reduce(
    (sum, matrix) => sum + matrix.totalSamples,
    0,
  );

  return (
    <div className="w-full">
      <header className="mx-auto max-w-3xl text-center">
        <p className="text-sm uppercase tracking-[0.3em] text-purple-300">
          HyperWhisper Cloud
        </p>
        <h1 className="mt-4 bg-gradient-to-r from-white to-gray-400 bg-clip-text text-4xl font-bold text-transparent md:text-6xl">
          {TITLE}
        </h1>
        <p className="mx-auto mt-5 text-lg text-gray-400">
          Every transcription we run times the provider that answered it. These
          are those timings — {totalSamples.toLocaleString()} provider attempts
          over the last {WINDOW_DAYS} days, grouped by the region that made the
          call.
        </p>
      </header>

      <LatencyMatrix defaultBucket={DEFAULT_BUCKET} matrices={matrices} />

      <section className="mx-auto mt-16 max-w-3xl border-t border-gray-800 pt-10">
        <h2 className="text-xl font-semibold text-white">How this is measured</h2>
        <dl className="mt-6 space-y-6 text-sm leading-6 text-gray-400">
          <div>
            <dt className="font-medium text-gray-200">What the number is</dt>
            <dd className="mt-1">
              The time one provider took to answer one call, measured at the edge
              machine that made it. Upload, authentication, and our own credit
              checks are excluded. So is the network between you and us — this is
              the provider&apos;s time, not your round trip.
            </dd>
          </div>
          <div>
            <dt className="font-medium text-gray-200">Failed attempts count</dt>
            <dd className="mt-1">
              A provider that times out still spent your time, so its attempt is
              in the latency numbers. When one provider fails we fall back to
              another, and both attempts are recorded separately. The error-rate
              metric shows how often that happens.
            </dd>
          </div>
          <div>
            <dt className="font-medium text-gray-200">Why clip length matters</dt>
            <dd className="mt-1">
              Longer audio takes longer to transcribe, so comparing a provider
              handed 5-second clips against one handed 5-minute files would say
              nothing. Every cell compares clips of similar length.
            </dd>
          </div>
          <div>
            <dt className="font-medium text-gray-200">What it covers</dt>
            <dd className="mt-1">
              The batch transcription endpoint. Live streaming transcription is
              not measured here.
            </dd>
          </div>
          <div>
            <dt className="font-medium text-gray-200">What we store</dt>
            <dd className="mt-1">
              A timing row carries the provider, the model, the region, the
              rounded clip length, which attempt in the chain it was, and whether
              the call worked. It carries no account, no key, no request id, and
              no audio or text, and its timestamp is stored only to the hour, so
              nothing ties a row to a person or reassembles one transcription.
            </dd>
          </div>
        </dl>
      </section>
    </div>
  );
}