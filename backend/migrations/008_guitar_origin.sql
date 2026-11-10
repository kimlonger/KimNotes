-- Keep reset links on the guitar frontend while reusing the shared accounts.
UPDATE applications SET public_origin='https://guitar.kimlulu.com'
WHERE id='guitar' AND public_origin IS NULL;
