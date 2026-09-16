import { site } from '../site';

export function Features() {
  return (
    <section className="band" aria-labelledby="features-title">
      <div className="container">
        <h2 id="features-title">Why {site.name}</h2>
        <ul className="features">
          {site.features.map((f) => (
            <li key={f.title}>
              <h3>{f.title}</h3>
              <p>{f.text}</p>
            </li>
          ))}
        </ul>
      </div>
    </section>
  );
}
