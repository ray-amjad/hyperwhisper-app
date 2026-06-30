/**
 * Escape a string for safe interpolation into HTML email markup.
 *
 * Values such as the customer name originate from attacker-controllable
 * sources (e.g. Stripe checkout `customer_details.name`, where a buyer sets
 * their own billing name). Interpolating them raw into the email HTML allows
 * markup/script/style injection into a transactional email sent from a trusted
 * sender. Always escape user-derived values before embedding them in HTML.
 */
export const escapeHtml = (value: string): string =>
    value
        .replace(/&/g, "&amp;")
        .replace(/</g, "&lt;")
        .replace(/>/g, "&gt;")
        .replace(/"/g, "&quot;")
        .replace(/'/g, "&#39;");
