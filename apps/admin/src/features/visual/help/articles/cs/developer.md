# Vývojářské komponenty

Když část potřebuje kód, vývojář ji může napsat v Reactu (TSX): **Moje komponenty › Nová vývojářská komponenta (TSX)**.

- Deklaruje nastavení, která přijímá, takže se vkládá a nastavuje jako každá jiná část.
- **Upravit zdrojový kód** otevře její kód.
- Z bezpečnostních důvodů její kód v samotném editoru nikdy neběží: na plátně se každá kreslí ve vlastním uzavřeném rámu (než se spustí, nebo když selže, ukazuje se zástupný blok). **Náhled** spouští celý web včetně kódu v odděleném sandboxu, stejně jako publikovaný web.
- Komponenta, která si sama načítá data, je ukáže v **Náhledu** a na webu, ne na plátně.

Používejte je na to, co vestavěné části neumí, a zbytek nechte vizuální.
