import {
  describe,
  it,
  expect,
  vi,
  beforeEach,
  afterEach,
} from "vitest";
import { render, screen, cleanup } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import TruncatedGuid from "./truncated-guid";

// useToast throws when not wrapped in a provider. The component contract under
// test doesn't depend on what the toast does — just that the right click path
// is taken — so a no-op stub keeps the tests focused.
const addToast = vi.fn();
vi.mock("components/ui/toast", () => ({
  useToast: () => ({ addToast }),
}));

// Tooltip is a passive wrapper; rendering it adds noise and a portal. Replace
// it with a thin passthrough.
vi.mock("components/ui/tooltip", () => ({
  Tooltip: ({ children }: { children: React.ReactNode }) => <>{children}</>,
}));

// Mock the clipboard helper rather than jsdom's navigator.clipboard — jsdom
// defines `clipboard` as a lazy getter that resists direct override.
const copyToClipboardMock = vi.fn();
vi.mock("lib/clipboard", () => ({
  copyToClipboard: (text: string) => copyToClipboardMock(text),
}));

const FULL_GUID = "cce3b12a-1234-5678-9abc-def012345678";

describe("TruncatedGuid", () => {
  beforeEach(() => {
    copyToClipboardMock.mockReset();
    copyToClipboardMock.mockResolvedValue(undefined);
    addToast.mockClear();
  });

  afterEach(() => {
    cleanup();
  });

  describe("rendering", () => {
    it("renders the value truncated with ellipsis when longer than displayLength", () => {
      render(<TruncatedGuid guid={FULL_GUID} />);
      // Default displayLength is 8 → "cce3b12a…"
      expect(screen.getByRole("button", { name: /cce3b12a/ }).textContent).toBe(
        "cce3b12a…",
      );
    });

    it("renders short values untruncated", () => {
      render(<TruncatedGuid guid="short" />);
      expect(screen.getByRole("button", { name: "short" }).textContent).toBe(
        "short",
      );
    });

    it("renders an em-dash placeholder for null/undefined/empty values", () => {
      const { rerender, container } = render(<TruncatedGuid guid={null} />);
      expect(container.textContent).toBe("—");
      rerender(<TruncatedGuid guid={undefined} />);
      expect(container.textContent).toBe("—");
      rerender(<TruncatedGuid guid="" />);
      expect(container.textContent).toBe("—");
    });
  });

  describe("click behavior", () => {
    it("invokes onClick with the full GUID when the value is clicked and a handler is provided", async () => {
      const user = userEvent.setup();
      const onClick = vi.fn();
      render(<TruncatedGuid guid={FULL_GUID} onClick={onClick} />);

      await user.click(screen.getByRole("button", { name: /cce3b12a/ }));

      expect(onClick).toHaveBeenCalledExactlyOnceWith(FULL_GUID);
      expect(copyToClipboardMock).not.toHaveBeenCalled();
    });

    it("falls back to clipboard copy when the value is clicked without an onClick handler", async () => {
      const user = userEvent.setup();
      render(<TruncatedGuid guid={FULL_GUID} />);

      await user.click(screen.getByRole("button", { name: /cce3b12a/ }));

      expect(copyToClipboardMock).toHaveBeenCalledExactlyOnceWith(FULL_GUID);
    });

    it("always copies to clipboard when the copy icon is clicked, even if onClick is provided", async () => {
      const user = userEvent.setup();
      const onClick = vi.fn();
      render(<TruncatedGuid guid={FULL_GUID} onClick={onClick} />);

      await user.click(
        screen.getByRole("button", { name: "Copy to clipboard" }),
      );

      expect(copyToClipboardMock).toHaveBeenCalledExactlyOnceWith(FULL_GUID);
      expect(onClick).not.toHaveBeenCalled();
    });

    it("does not render the copy icon when withCopy is false", () => {
      render(<TruncatedGuid guid={FULL_GUID} withCopy={false} />);
      expect(
        screen.queryByRole("button", { name: "Copy to clipboard" }),
      ).toBeNull();
    });

    it("stops click propagation so wrapping row-level handlers don't fire", async () => {
      const user = userEvent.setup();
      const onClick = vi.fn();
      const outerClick = vi.fn();
      render(
        <div onClick={outerClick}>
          <TruncatedGuid guid={FULL_GUID} onClick={onClick} />
        </div>,
      );

      await user.click(screen.getByRole("button", { name: /cce3b12a/ }));
      await user.click(
        screen.getByRole("button", { name: "Copy to clipboard" }),
      );

      expect(onClick).toHaveBeenCalledExactlyOnceWith(FULL_GUID);
      expect(copyToClipboardMock).toHaveBeenCalledExactlyOnceWith(FULL_GUID);
      expect(outerClick).not.toHaveBeenCalled();
    });
  });
});
