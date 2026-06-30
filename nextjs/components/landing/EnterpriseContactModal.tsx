"use client";

import { Modal, ModalContent, ModalHeader, ModalBody } from "@heroui/modal";
import { Mail, Building2 } from "lucide-react";

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
        base: "bg-gray-900 border border-gray-800",
        header: "border-b border-gray-800",
        body: "py-6",
      }}
      isOpen={isOpen}
      size="lg"
      onClose={onClose}
    >
      <ModalContent>
        <ModalHeader className="flex flex-col gap-1 items-center pt-8 pb-4">
          {/* Icon */}
          <div className="w-16 h-16 mb-4 flex items-center justify-center rounded-full bg-gradient-to-br from-orange-500/20 to-amber-500/20 border border-orange-500/30">
            <Building2 className="w-8 h-8 text-orange-400" />
          </div>

          <h3 className="text-2xl font-bold text-white mb-2">Contact Sales</h3>
          <p className="text-gray-400 text-center max-w-md">
            Get in touch to learn more about enterprise pricing, volume
            discounts, and custom integrations.
          </p>
        </ModalHeader>

        <ModalBody className="space-y-4 pb-8">
          {/* Email display box */}
          <div className="rounded-lg border border-gray-700 bg-gray-800/50 p-4">
            <p className="text-sm text-gray-400 mb-2">Email us at:</p>
            <p className="text-lg font-semibold text-white">{emailAddress}</p>
          </div>

          {/* Email client options */}
          <div className="space-y-3">
            <p className="text-sm font-medium text-gray-300">
              Open in your email client:
            </p>

            {/* Default email client */}
            <a
              className="flex w-full items-center justify-center gap-2 rounded-lg bg-gradient-to-r from-orange-600 to-amber-600 px-6 py-3 text-base font-semibold text-white transition-all hover:from-orange-500 hover:to-amber-500 hover:shadow-lg"
              href={mailtoLink}
            >
              <Mail className="h-4 w-4" />
              Default Email Client
            </a>

            {/* Gmail and Outlook options */}
            <div className="grid grid-cols-2 gap-2">
              <a
                className="flex items-center justify-center gap-2 rounded-lg border border-gray-700 bg-gray-800 px-4 py-2.5 text-sm font-medium text-gray-300 transition-colors hover:bg-gray-700 hover:border-gray-600"
                href={`https://mail.google.com/mail/?view=cm&fs=1&to=${emailAddress}&su=${encodeURIComponent(subject)}&body=${encodeURIComponent(body)}`}
                rel="noopener noreferrer"
                target="_blank"
              >
                <svg
                  className="h-4 w-4"
                  fill="#EA4335"
                  role="img"
                  viewBox="0 0 24 24"
                >
                  <path d="M24 5.457v13.909c0 .904-.732 1.636-1.636 1.636h-3.819V11.73L12 16.64l-6.545-4.91v9.273H1.636A1.636 1.636 0 0 1 0 19.366V5.457c0-2.023 2.309-3.178 3.927-1.964L12 9.545l8.073-6.052C21.69 2.28 24 3.434 24 5.457z" />
                </svg>
                Gmail
              </a>
              <a
                className="flex items-center justify-center gap-2 rounded-lg border border-gray-700 bg-gray-800 px-4 py-2.5 text-sm font-medium text-gray-300 transition-colors hover:bg-gray-700 hover:border-gray-600"
                href={`https://outlook.live.com/mail/0/deeplink/compose?to=${emailAddress}&subject=${encodeURIComponent(subject)}&body=${encodeURIComponent(body)}`}
                rel="noopener noreferrer"
                target="_blank"
              >
                <Mail className="h-4 w-4 text-blue-400" />
                Outlook
              </a>
            </div>
          </div>
        </ModalBody>
      </ModalContent>
    </Modal>
  );
}
