# Přihlašování

Přihlášení umožňují dva pluginy a každý je pro jiné lidi:

- **Uživatelé webu** (*User Authentication*): zaměstnanci, partneři nebo klienti, které váš workspace pozve — nebo pustí podle e-mailové domény jejich organizace. Přihlašují se na přihlašovací stránce workspace (e-mail a heslo, Google, Microsoft…), takže web potřebuje jen tlačítko. Sami se registrovat nemohou.
- **Účty návštěvníků** (*Visitor Authentication*): veřejnost, která se registruje sama — členové, zákazníci, komunita. Web ukazuje formulář s e-mailem a heslem a registrační formulář.

Součásti (hledejte *sign in*), u každé nastavte **Accounts** na druh, který používáte:

- **Sign-in**: pro uživatele webu tlačítko na přihlašovací stránku; pro účty návštěvníků přihlašovací formulář, nebo s **Does: Create an account** registrační formulář. **Then open** pošle lidi dál, např. na `/account`.
- **Signed-in section**: její obsah vidí jen přihlášení — nebo s **Show to: Signed-out people** jen ti ostatní: menu pro členy, „Přihlaste se a uvidíte víc“.
- **Signed-in user**: jméno přihlášeného a tlačítko **Sign out**, do hlavičky.

Skrýt neznamená ochránit: obsah sekce pro přihlášené je pořád ve stránce. Soukromou stránku chraňte její cestou v **Access → Site access** pluginu User Authentication a její data v **Access → API access**. Tabulka Dynamic Apps s veřejným přístupem *own* ukáže každému návštěvníkovi jen jeho záznamy.

**V náhledu:** přihlaste se jako uživatel webu, nebo použijte **View as**, z lišty náhledu. Návštěvníci, kteří se na stránce přihlásí nebo zaregistrují, jsou testovací účty v sandboxu náhledu; menu návštěvníka je odhlásí a **Smazat testovací data** je odstraní.

Přihlášení nikdy nestavte z **Form** a jeho polí: formulář pluginu Forms uloží, co dostane, včetně hesla, do své schránky. Kontroly to odmítnou.
