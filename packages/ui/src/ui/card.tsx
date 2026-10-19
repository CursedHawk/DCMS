import { cn } from '../cn';

export function Card({ className, ...props }: React.HTMLAttributes<HTMLDivElement>) {
  return (
    <div
      className={cn('rounded-lg border bg-card text-card-foreground shadow-sm', className)}
      {...props}
    />
  );
}

/*
 * Padding lives on the content, not the header: `CardContent` is `p-5` on its own, and a header
 * above it leaves its bottom edge to the content's top. It used to be the other way round
 * (`p-5 pt-0` content), which was only right under a header — and most cards have none, so
 * fifteen call sites overrode it back to `p-5`.
 */
export function CardHeader({ className, ...props }: React.HTMLAttributes<HTMLDivElement>) {
  return <div className={cn('flex flex-col gap-1.5 p-5 pb-0', className)} {...props} />;
}

export function CardTitle({
  className,
  children,
  ...props
}: React.HTMLAttributes<HTMLHeadingElement>) {
  // `children` is destructured rather than spread so that a heading with no content is a
  // type error and a lint error here, instead of an empty <h3> a screen reader announces
  // as a section boundary that is not there.
  return (
    <h3 className={cn('font-semibold leading-tight tracking-tight', className)} {...props}>
      {children}
    </h3>
  );
}

export function CardContent({ className, ...props }: React.HTMLAttributes<HTMLDivElement>) {
  return <div className={cn('p-5', className)} {...props} />;
}
