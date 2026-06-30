"use client";

import { useState, useEffect } from "react";
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

export default function DownloadModal() {
  const { isOpen, closeModal } = useDownloadModal();
  const t = useTranslations("downloadModal");
  const router = useRouter();
  const [email, setEmail] = useState("");
  const [error, setError] = useState<string | null>(null);

  const recordDownload = api.download.recordDownload.useMutation({
    onSuccess: () => {
      router.push("/download");
      closeModal();
    },
    onError: (err) => {
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
    router.push("/download");
    closeModal();
  };

  return (
    <Modal
      classNames={{
        base: "bg-gray-900 border border-gray-800",
        header: "border-b border-gray-800",
        body: "py-6",
        footer: "border-t border-gray-800",
      }}
      isOpen={isOpen}
      size="lg"
      onClose={closeModal}
    >
      <ModalContent>
        <ModalHeader className="flex flex-col gap-1 items-center pt-8 pb-4">
          {/* App icon */}
          <div className="w-24 h-24 mb-6 relative">
            <div className="w-full h-full bg-gradient-to-b from-gray-700 to-gray-900 rounded-2xl flex items-center justify-center shadow-2xl">
              <Image
                alt={t("logoAlt")}
                className="w-24 h-24 rounded-xl"
                height={96}
                src="/icon/256.png"
                width={96}
              />
            </div>
          </div>

          <h3 className="text-2xl font-bold text-white mb-2">{t("title")}</h3>
        </ModalHeader>

        <ModalBody className="py-4 space-y-2 text-center">
          <Input
            classNames={{
              base: "w-full",
              inputWrapper:
                "bg-black border-2 border-blue-500/50 hover:border-blue-500 focus-within:!border-blue-500",
              input: "text-white placeholder:text-gray-500",
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
          {error ? (
            <p aria-live="polite" className="text-sm text-red-500" role="alert">
              {error}
            </p>
          ) : (
            <p className="text-sm text-gray-400">{t("description")}</p>
          )}
        </ModalBody>

        <ModalFooter className="pt-4 pb-6 flex-col gap-3">
          <Button
            fullWidth
            className="bg-gray-200 text-black font-semibold hover:bg-white transition-colors"
            isDisabled={!email}
            isLoading={recordDownload.isPending}
            size="lg"
            onClick={handleDownload}
          >
            {t("downloadButton")}
          </Button>
          <Button
            fullWidth
            className="text-gray-300 hover:text-white transition-colors"
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
