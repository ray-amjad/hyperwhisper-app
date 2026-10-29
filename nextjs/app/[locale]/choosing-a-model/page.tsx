import { notFound } from "next/navigation";

import ModelPicker from "@/components/choosing-a-model/ModelPicker";
import { getMeasuredLatency } from "@/src/content/choosing-a-model";
import { CLOUD_MODELS, DEVICE_MODELS } from "@/lib/choosing-a-model/catalog";
import { englishStaticPageMetadata } from "@/lib/site-metadata";
import { DuskPage, PageHero } from "@/components/landing/DuskPage";
import { WRAP } from "@/components/landing/dusk";
import { GLASS, LABEL, SECTION } from "@/components/landing/dusk-pages";
import Reveal from "@/components/landing/Reveal";

/** An inline link in the explanatory copy. */
const PROSE_LINK =
  "rounded text-[var(--dusk-lav)] underline decoration-[rgba(196,181,253,.4)] underline-offset-4 transition-colors hover:text-white hover:decoration-white focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--dusk-lav)]";

export const dynamic = "force-static";
export const revalidate = 3600;

const TITLE = "Choosing a speech-to-text model";
const DESCRIPTION =
  "Tell us what matters — accuracy, speed, cost or privacy — and see which of the speech-to-text models HyperWhisper ships actually fits, cloud and on-device ranked side by side.";

// #815: the English-only check runs here too. A notFound() thrown only from the
// page left this English metadata (and the layout's "index, follow") in the RSC
// payload, which is what the browser shows on /de/choosing-a-model. Thrown here, Next
// resolves the [locale] 404's metadata instead.
export async function generateMetadata({ params }: Props) {
  const { locale } = await params;
  if (locale !== "en") {
    notFound();
  }

  return englishStaticPageMetadata({
    path: "/choosing-a-model",
    title: TITLE,
    description: DESCRIPTION,
  });
}

type Props = {
  params: Promise<{ locale: string }>;
};

/**
 * The model chooser. English only, like /latency and the blog — the page is
 * mostly numbers, and translating 40 copies of them buys nothing.
 *
 * Static with an hourly revalidate. The measured timings behind it are a 90-day
 * aggregate, so an hour-old page tells the same story as a fresh one, and the
 * one thing that really is per-visitor — which region they are closest to — is
 * fetched by the client from a small dynamic endpoint after paint.
 */
export default async function ChoosingAModelPage({ params }: Props) {
  const { locale } = await params;
  if (locale !== "en") {
    notFound();
  }

  const { byRegion, samplesByRegion } = await getMeasuredLatency();

  // Busiest regions first, so the default selection is a region we know well
  // and the dropdown opens on something useful even if geolocation is blocked.
  //
  // Busiest by attempts, not by how many providers cleared the display floor:
  // a region with twenty cells of three calls knows less than one with eight
  // cells of five thousand, and regions[0] is what a reader keeps when the
  // geolocation call fails. Ties break alphabetically so the static build is
  // reproducible.
  const regions = Object.keys(byRegion).sort((a, b) => {
    const byVolume = (samplesByRegion[b] ?? 0) - (samplesByRegion[a] ?? 0);
    return byVolume !== 0 ? byVolume : a.localeCompare(b);
  });

  return (
    <DuskPage>
      {/*
        Deliberately two lines. The trade-off this page is about — push one
        priority up and the others give way — used to be explained here in a
        paragraph, above the control that demonstrates it in about two seconds
        of dragging. The budget still names itself where it lives ("Your 100
        points", in ModelPicker), so nothing is left unexplained by cutting the
        explanation from the top.

        What the sub-head must keep is the claim that these are the models
        HyperWhisper ships. It is the only sentence that says why this page
        exists rather than a link to the Artificial Analysis leaderboard.
      */}
      {/*
        "across macOS and Windows" is load-bearing, not padding.
        DEVICE_MODELS is the union over both platforms, while the ranked list
        below shows only the on-device rows for the platform the reader has
        selected — always the smaller set. Without the qualifier the sentence
        reads as a count of what is on screen, and then visibly disagrees with
        the table's own "N cloud · N on-device" caption a scroll later.

        Deliberately no numbers here. Every count on this page is derived from
        the catalog at render time, so writing one into a comment only creates
        a second place to be wrong the next time a model ships — which is how
        this comment came to quote a cloud count that was already stale.
      */}
      <PageHero
        eyebrow="Choosing a model"
        lead={
          <>
            Tell us what matters and we will rank the {CLOUD_MODELS.length}{" "}
            cloud and {DEVICE_MODELS.length} on-device models HyperWhisper ships
            across macOS and Windows. Nothing here is a general leaderboard.
          </>
        }
        title="Which model should you use?"
      />

      <section className={`${WRAP} ${SECTION}`}>
        <ModelPicker measured={byRegion} regions={regions} />
      </section>

      <section className={`${WRAP} pb-20 md:pb-[110px]`}>
        <Reveal className="mx-auto max-w-[900px]">
          <p className={LABEL}>Cloud or on-device</p>
          <h2 className="mt-2.5 text-[28px] font-semibold leading-[1.15] tracking-[-0.03em] text-white md:text-[34px]">
            Cloud and on-device are different bargains
          </h2>
          <p className="mt-4 text-[15px] leading-[1.6] text-[var(--dusk-mute)]">
            Every row on this page is one or the other, and the badge says
            which. The distinction is not a detail — it changes what you pay,
            what you wait for, and where your voice ends up.
          </p>
          <div className="mt-6 grid gap-4 md:grid-cols-2">
            <div className={`${GLASS} p-6 md:p-[26px]`}>
              <h3 className="text-[17px] font-semibold text-[#bfdbfe]">
                Cloud models
              </h3>
              <p className="mt-2 text-[14.5px] leading-[1.6] text-[var(--dusk-mute)]">
                Your audio is uploaded to HyperWhisper Cloud, which hands it to
                the provider and sends the text back. You get the strongest
                accuracy available and no download, and you pay per audio minute
                in credits. These are the models with published error rates,
                because a hosted model is something a third party can measure.
              </p>
            </div>
            <div className={`${GLASS} p-6 md:p-[26px]`}>
              <h3 className="text-[17px] font-semibold text-[#a7f3c4]">
                On-device models
              </h3>
              <p className="mt-2 text-[14.5px] leading-[1.6] text-[var(--dusk-mute)]">
                The model is downloaded once and runs on your own Mac or PC. The
                audio never leaves the machine, there is nothing to pay per
                minute, and it works with no network at all. What you give up is
                the top of the accuracy table, and a few gigabytes of disk.
              </p>
            </div>
          </div>
        </Reveal>
      </section>

      <section className={`${WRAP} pb-20 md:pb-[110px]`}>
        <Reveal className="mx-auto max-w-[900px]">
          <p className={LABEL}>Method</p>
          <h2 className="mt-2.5 text-[28px] font-semibold leading-[1.15] tracking-[-0.03em] text-white md:text-[34px]">
            Where these numbers come from
          </h2>
          <dl
            className={`mt-6 divide-y divide-[var(--dusk-line)] text-[15px] leading-[1.65] text-[var(--dusk-mute)] ${GLASS}`}
          >
            <div className="p-6 md:px-[26px]">
              <dt className="font-semibold text-white">Accuracy</dt>
              <dd className="mt-1">
                Word error rate comes from the{" "}
                <a
                  className={PROSE_LINK}
                  href="https://artificialanalysis.ai/speech-to-text/non-streaming"
                  rel="noopener noreferrer"
                  target="_blank"
                >
                  Artificial Analysis speech-to-text leaderboard
                </a>{" "}
                — an independent third party, not us. We do not publish an
                accuracy claim of our own here. A model they have not measured
                shows a dash and is marked{" "}
                <span className="text-white">not benchmarked</span>; it scores
                neutrally rather than being flattered by a number we invented.
              </dd>
            </div>
            <div className="p-6 md:px-[26px]">
              <dt className="font-semibold text-white">
                Why some on-device models borrow a cloud model&apos;s score
              </dt>
              <dd className="mt-1">
                Whisper and Parakeet are open weights. The leaderboard measured
                the same weight files we download, just running on someone
                else&apos;s hardware — so the error rate carries over and is
                marked <span className="text-white">same weights</span>. Speed
                does not carry over, which is why those rows never borrow a
                speed figure. The remaining local models have no published
                benchmark at all and fall back to the app&apos;s own accuracy
                rating, marked <span className="text-white">app rating</span>.
                Without that fallback the smallest, roughest models would score
                like frontier ones purely for being free and private.
              </dd>
            </div>
            <div className="p-6 md:px-[26px]">
              <dt className="font-semibold text-white">Cost</dt>
              <dd className="mt-1">
                Credits per audio minute, read from the same catalog the desktop
                apps read, so the price here is the price the app charges you.
                1,000 credits are $1, which makes a model at 4.5 credits a
                minute $4.50 per 1,000 minutes of audio. On-device models cost
                nothing to run, so they score full marks on cost no matter how
                you weight it.
              </dd>
            </div>
            <div className="p-6 md:px-[26px]">
              <dt className="font-semibold text-white">Speed</dt>
              <dd className="mt-1">
                Two numbers, and the difference matters.{" "}
                <span className="text-white">Per audio minute</span> is how long
                a minute of speech takes to come back, from the
                leaderboard&apos;s published speed factor; on-device rows
                estimate it from the app&apos;s own speed rating, so read those
                as an ordering, not a promise. That is the number the ranking
                uses, because it is the only one we hold for every model.{" "}
                <span className="text-white">Measured clip</span> is our own
                median from the last 90 days — the same measurements behind{" "}
                <a className={PROSE_LINK} href="/en/latency">
                  the latency page
                </a>
                , taken from clips under ten seconds because that is what
                dictation is. It is a round trip for one clip, not a throughput:
                most of it is the network and the provider&apos;s fixed overhead
                rather than decoding, and we do not store the clip&apos;s
                length, so it cannot be scaled up to a minute. Feeding it into
                the ranking anyway would punish the providers we happen to know
                most about, so it sits in its own column instead.
              </dd>
            </div>
            <div className="p-6 md:px-[26px]">
              <dt className="font-semibold text-white">Privacy</dt>
              <dd className="mt-1">
                Three steps, by where the audio goes. On-device scores full
                marks: nothing is transmitted. A model you can use with your own
                API key scores half: the audio still reaches the vendor, but on
                your account rather than through us. A cloud-tier-only model
                scores lowest. Turn the privacy slider up and the ranking moves
                to on-device models, which is the honest answer to that
                question.
              </dd>
            </div>
            <div className="p-6 md:px-[26px]">
              <dt className="font-semibold text-white">Your region</dt>
              <dd className="mt-1">
                HyperWhisper Cloud runs in 17 regions and routes you to the
                nearest one. We ask a small endpoint which of those you are
                closest to so the speed numbers are the ones you would actually
                get; the answer is used once to pick a row and is never stored.
                You can override it with the dropdown. Off our edge — a local
                dev server, say — nothing is detected and the busiest region is
                selected instead.
              </dd>
            </div>
            <div className="p-6 md:px-[26px]">
              <dt className="font-semibold text-white">
                What this page will not tell you
              </dt>
              <dd className="mt-1">
                A ranking is not a recommendation for a job it has never seen.
                If you dictate medical or legal terms, transcribe heavy accents,
                or need speaker labels, the model that wins here on paper may
                still lose on your audio. Every model listed is switchable in
                Settings → Transcription, so the last word is a minute of your
                own speech, not a number on a website.
              </dd>
            </div>
          </dl>
        </Reveal>
      </section>
    </DuskPage>
  );
}
