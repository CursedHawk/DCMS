# Záznamy z Dynamic Apps

Aplikace postavená v **Dynamic Apps** — CRM, rezervační systém, sklad — ukládá **záznamy** do tabulek. Web je ukazuje a nechá návštěvníky je přidávat a měnit, nakolik to dovoluje **veřejný přístup** aplikace.

- **Seznam:** **Data table** (hledejte *data table*) s nastavenou **App** a **Table**. Návštěvníci hledají, řadí, filtrují a ukládají pohledy. Do **Open a record at** zadejte stránku jako `/deals/:id` a každý řádek povede na svůj záznam.
- **Karty:** **Collection**, jejímž obsahem (**Content**) je tabulka aplikace.
- **Jeden záznam:** přidejte stránku s adresou jako `/deals/:id` a v Pages jí jako obsah detailní stránky zvolte tabulku; její části ukazují pole záznamu. Collection nad souvisejícími záznamy (obchody zákazníka) na ní ukáže ty tohoto záznamu.
- **Přidávání a úpravy:** **Record form** s aplikací a tabulkou. **Does: Edit the page’s record** na detailní stránce mění záznam, který stránka ukazuje; **Then open** po uložení otevře třeba `/deals/:id`.

Kdo smí záznamy tabulky číst, přidávat nebo měnit, se nastavuje na obrazovce **Konfigurace** aplikace, ne na stránce. Tabulka, kterou web číst nesmí, se v nabídkách neobjeví: publikujte ji, nebo otevřete její veřejný přístup.

**Agent** aplikace zná: požádejte ho o CRM a může navrhnout tabulky — vy je schválíte a pak publikujete na obrazovce Konfigurace aplikace.
