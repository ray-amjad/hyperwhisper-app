# HyperWhisper website

The Next.js site behind [hyperwhisper.com](https://www.hyperwhisper.com):
marketing pages, blog, docs rewrites, accounts, checkout, licence and credit
APIs.

It runs on Cloudflare Workers through
[OpenNext](https://opennext.js.org/cloudflare). `.github/workflows/nextjs-deploy.yml`
builds and deploys it on every push to `main`. The runbook is
[`DEPLOY-CLOUDFLARE.md`](./DEPLOY-CLOUDFLARE.md).

```bash
pnpm install
pnpm dev       # next dev, no Workers runtime
pnpm preview   # the real Worker in workerd (see DEPLOY-CLOUDFLARE.md section 4)
pnpm test
```
