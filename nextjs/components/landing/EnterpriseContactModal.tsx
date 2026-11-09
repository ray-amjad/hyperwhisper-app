"use client";

import { Modal, ModalContent, ModalHeader, ModalBody } from "@heroui/modal";
import { Mail, Building2 } from "lucide-react";

import { BTN_LG, BTN_WHITE } from "./dusk";
import { GmailIcon, OutlookIcon } from "./MailClientIcons";

interface EnterpriseContactModalProps {
  isOpen: boolean;
  onClose: () => void;
}

export default function EnterpriseContactModal({
  isOpen,
  onClose,
}: EnterpriseContactModalProps) {
  const emailAddress = "sales@hyperwhisper.com";
  const subject = "Enterprise & Custom Pricing Inquiry";
  const body = `Hi,

I'm interested in learning more about enterprise pricing and custom solutions for HyperWhisper.

Company: [Your Company]
Team Size: [Number of people]
Use Case: [Brief description]

[Your message here]`;

  const mailtoLink = `mailto:${emailAddress}?subject=${encodeURIComponent(subject)}&body=${encodeURIComponent(body)}`;

  return (
    <Modal
      classNames={{
        base: "bg-[#0a0c1d] border border-[var(--dusk-line)]",
        header: "border-b border-[var(--dusk-line)]",
        body: "py-6",
      }}
      isOpen={isOpen}
      size="lg"
      onClose={onClose}
    >
      <ModalContent>
        <ModalHeader className="flex flex-col gap-1 items-center pt-8 pb-4">
          {/* Icon */}
          <div className="w-16 h-16 mb-4 flex items-center justify-center rounded-full bg-[rgba(196,181,253,.12)] border border-[rgba(196,181,253,.3)]">
            <Building2 className="w-8 h-8 text-[var(--dusk-lav)]" />
          </div>

          <h3 className="text-2xl font-semibold tracking-[-0.02em] text-white mb-2">Contact Sales</h3>
          <p className="text-[15px] font-normal leading-[1.55] text-[var(--dusk-mute)] text-center max-w-md">
            Get in touch to learn more about enterprise pricing, volume
            discounts, and custom integrations.
          </p>
        </ModalHeader>

        <ModalBody className="space-y-4 pb-8">
          {/* Email display box */}
          <div className="rounded-2xl border border-[var(--dusk-line)] bg-[var(--dusk-glass)] p-4">
            <p className="text-sm text-[var(--dusk-mute)] mb-2">Email us at:</p>
            <p className="text-lg font-semibold text-white">{emailAddress}</p>
          </div>

          {/* Email client options */}
          <div className="space-y-3">
            <p className="text-sm font-medium text-[var(--dusk-mute)]">
              Open in your email client:
            </p>

            {/* Default email client */}
            <a className={`${BTN_WHITE} ${BTN_LG} w-full`} href={mailtoLink}>
              <Mail className="h-4 w-4" />
              Default Email Client
            </a>

            {/* Gmail and Outlook options */}
            <div className="grid grid-cols-2 gap-2">
              <a
                className="flex items-center justify-center gap-2 rounded-full border border-white/[.16] bg-[rgba(10,10,24,.5)] px-4 py-2.5 text-sm font-medium text-white transition-colors hover:bg-white/[.07] focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--dusk-lav)]"
                href={`https://mail.google.com/mail/?view=cm&fs=1&to=${emailAddress}&su=${encodeURIComponent(subject)}&body=${encodeURIComponent(body)}`}
                rel="noopener noreferrer"
                target="_blank"
              >
                <GmailIcon />
                Gmail
              </a>
              <a
                className="flex items-center justify-center gap-2 rounded-full border border-white/[.16] bg-[rgba(10,10,24,.5)] px-4 py-2.5 text-sm font-medium text-white transition-colors hover:bg-white/[.07] focus-visible:outline-2 focus-visible:outline-offset-2 focus-visible:outline-[var(--dusk-lav)]"
                href={`https://outlook.live.com/mail/0/deeplink/compose?to=${emailAddress}&subject=${encodeURIComponent(subject)}&body=${encodeURIComponent(body)}`}
                rel="noopener noreferrer"
                target="_blank"
              >
                <OutlookIcon />
                Outlook
              </a>
            </div>
          </div>
        </ModalBody>
      </ModalContent>
    </Modal>
  );
}
