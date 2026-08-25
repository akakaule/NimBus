import { describe, it, expect, vi, beforeEach, afterEach } from "vitest";
import { render, screen, cleanup } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { CopyButton } from "./copy-button";

// useToast throws outside a provider, and what the toast renders isn't the
// contract under test — only that the right branch was taken.
const addToast = vi.fn();
vi.mock("components/ui/toast", () => ({
  useToast: () => ({ addToast }),
}));

// Mock the helper rather than jsdom's navigator.clipboard, which is defined as
// a lazy getter that resists direct override.
const copyToClipboardMock = vi.fn();
vi.mock("lib/clipboard", () => ({
  copyToClipboard: (text: string) => copyToClipboardMock(text),
}));

describe("CopyButton", () => {
  beforeEach(() => {
    copyToClipboardMock.mockReset();
    copyToClipboardMock.mockResolvedValue(undefined);
    addToast.mockClear();
  });

  afterEach(cleanup);

  it("copies exactly the text it was given", async () => {
    render(<CopyButton text={'{\n  "a": 1\n}'} describes="Payload" />);

    await userEvent.click(screen.getByRole("button", { name: /copy payload/i }));

    // Byte-for-byte: an operator pasting this into a resubmit or a ticket needs
    // the payload as rendered, not a re-serialized approximation of it.
    expect(copyToClipboardMock).toHaveBeenCalledWith('{\n  "a": 1\n}');
    expect(addToast).toHaveBeenCalledWith(
      expect.objectContaining({ variant: "success" }),
    );
  });

  it("copies the masked text when that is what was rendered", async () => {
    // The redaction guarantee: the button is handed the on-screen payload, so a
    // caller without the PII Reader role can never copy past the server's mask.
    render(<CopyButton text={'{ "Email": "[REDACTED]" }'} describes="Payload" />);

    await userEvent.click(screen.getByRole("button", { name: /copy payload/i }));

    expect(copyToClipboardMock).toHaveBeenCalledWith('{ "Email": "[REDACTED]" }');
  });

  it("reports a failure instead of silently doing nothing", async () => {
    copyToClipboardMock.mockRejectedValue(new Error("denied"));
    render(<CopyButton text="x" describes="Payload" />);

    await userEvent.click(screen.getByRole("button", { name: /copy payload/i }));

    expect(addToast).toHaveBeenCalledWith(
      expect.objectContaining({ variant: "error" }),
    );
  });
});
