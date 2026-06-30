export default function BlogLayout({
  children,
}: {
  children: React.ReactNode;
}) {
  return (
    <section className="flex flex-col items-center justify-center gap-6 px-6 py-10 md:py-14">
      <div className="w-full max-w-6xl">{children}</div>
    </section>
  );
}
