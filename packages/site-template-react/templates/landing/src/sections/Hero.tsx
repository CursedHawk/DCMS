import { site } from '../site';

export function Hero() {
  return (
    <section className="hero-band">
      <div className="container hero-band__inner">
        <p className="eyebrow">{site.name}</p>
        <h1>{site.headline}</h1>
        <p className="lead">{site.intro}</p>
        <a className="button" href="#contact">
          {site.cta}
        </a>
      </div>
    </section>
  );
}
