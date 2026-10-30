"use client";

import { useState, useEffect, useId, useRef } from "react";
import {
  Modal,
  ModalContent,
  ModalHeader,
  ModalBody,
  ModalFooter,
} from "@heroui/modal";
import { Button } from "@heroui/button";
import { Input } from "@heroui/input";
import Image from "next/image";
import { useTranslations } from "next-intl";

import { useRouter } from "@/src/i18n/navigation";
import { useDownloadModal } from "@/contexts/DownloadModalContext";
import { api } from "@/lib/trpc/client";
import { isInputValidationError } from "@/lib/trpc/input-error";
import { ErrorReveal, shake } from "@/components/ErrorReveal";

export default function DownloadModal() {
  const { isOpen, platform, closeModal } = useDownloadModal();
  const t = useTranslations("downloadModal");
  const router = useRouter();
  const [email, setEmail] = useState("");
  const [error, setError] = useState<string | null>(null);
  // #968: the field points at whichever line below is on screen.
  const descriptionId = useId();
  const errorId = useId();
  // #2286: the field shakes when the address itself is what was refused.
  const field = useRef<HTMLDivElement>(null);
  // A homepage platform button carries its platform through to /download,
  // which still resolves the download URL from the appcast. A plain Download
  // button leaves the OS detection on /download in charge.
  const downloadPath = platform
    ? `/download?platform=${platform}`
    : "/download";

  const recordDownload = api.download.recordDownload.useMutation({
    onSuccess: () => {
      router.push(downloadPath);
      closeModal();
    },
    onError: (err) => {
      if (isInputValidationError(err)) {
        setError(t("errorEmail"));
        shake(field.current);

        return;
      }
      setError(err.message || t("errorGeneric"));
    },
  });

  useEffect(() => {
    if (!isOpen) {
      setEmail("");
      setError(null);
    }
  }, [isOpen]);

  const handleDownload = () => {
    if (!email) return;
    setError(null);
    recordDownload.mutate({ email });
  };

  const handleSkipAndDownload = () => {
    setError(null);
    router.push(downloadPath);
    closeModal();
  };

  return (
    <Modal
      classNames={{
        base: "bg-[#0a0c1d] border border-[var(--dusk-line)]",
        header: "border-b border-[var(--dusk-line)]",
        body: "py-6",
        footer: "border-t border-[var(--dusk-line)]",
      }}
      isOpen={isOpen}
      size="lg"
      onClose={closeModal}
    >
      <ModalContent>
        <ModalHeader className="flex flex-col gap-1 items-center pt-8 pb-4">
          {/* App icon */}
          <div className="w-24 h-24 mb-6 relative">
            <Image
              alt={t("logoAlt")}
              className="w-24 h-24 rounded-[20%] outline-1 -outline-offset-1 outline-white/10"
              height={96}
              src="/icon/256.png"
              width={96}
            />
          </div>

          <h3 className="text-2xl font-bold text-white mb-2">{t("title")}</h3>
        </ModalHeader>

        <ModalBody className="py-4 space-y-2 text-center">
          <div ref={field} className="t-input">
            <Input
              aria-describedby={error ? errorId : descriptionId}
              aria-label={t("emailPlaceholder")}
              autoComplete="email"
              classNames={{
                base: "w-full",
                inputWrapper:
                  "bg-[var(--dusk-night)] border-2 border-[#c4b5fd]/40 hover:border-[#c4b5fd]/70 focus-within:!border-[#c4b5fd]",
                input: "text-white placeholder:text-gray-400",
              }}
              placeholder={t("emailPlaceholder")}
              size="lg"
              type="email"
              value={email}
              onChange={(e) => setEmail(e.target.value)}
              onKeyDown={(e) => {
                if (e.key === "Enter" && email) {
                  handleDownload();
                }
              }}
            />
          </div>
          {/* #2286: the error reveals in place of the description instead of
              arriving in one frame. The description still unmounts while the
              error shows; min-h-5 (one text-sm line) holds its space, so the
              footer does not jump up before the error grows. */}
          <div className="min-h-5">
            <ErrorReveal>
              {error ? (
                <p
                  aria-live="polite"
                  className="text-sm text-red-500"
                  id={errorId}
                  role="alert"
                >
                  {error}
                </p>
              ) : null}
            </ErrorReveal>
            {error ? null : (
              <p className="text-sm text-gray-400" id={descriptionId}>
                {t("description")}
              </p>
            )}
          </div>
        </ModalBody>

        <ModalFooter className="pt-4 pb-6 flex-col gap-3">
          <Button
            fullWidth
            className="rounded-full bg-white text-[#0b0b14] font-semibold hover:bg-[#ececf5] transition-colors"
            isDisabled={!email}
            isLoading={recordDownload.isPending}
            size="lg"
            onClick={handleDownload}
          >
            {t("downloadButton")}
          </Button>
          <Button
            fullWidth
            className="rounded-full text-gray-300 hover:text-white transition-colors"
            isDisabled={recordDownload.isPending}
            size="lg"
            variant="light"
            onClick={handleSkipAndDownload}
          >
            {t("skipAndDownloadButton")}
          </Button>
        </ModalFooter>
      </ModalContent>
    </Modal>
  );
}
