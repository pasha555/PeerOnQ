import { useState, useEffect } from "react";
import { PageHeader } from "@/components/PageHeader";
import { EmptyState } from "@/components/EmptyState";
import { ConfirmDialog } from "@/components/ConfirmDialog";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Search, Plus, BookUser, Star, Users, Folder, MoreVertical, Pencil, Trash2 } from "lucide-react";
import { contactRepository } from "@/features/address-book/contactRepository";
import type { Contact, ContactGroup } from "@/types";
import { Dialog, DialogContent, DialogDescription, DialogFooter, DialogHeader, DialogTitle } from "@/components/ui/dialog";
import { DropdownMenu, DropdownMenuContent, DropdownMenuItem, DropdownMenuTrigger } from "@/components/ui/dropdown-menu";
import { Form, FormControl, FormDescription, FormField, FormItem, FormLabel, FormMessage } from "@/components/ui/form";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { deviceIdSchema, contactNameSchema } from "@/lib/validation";
import { DeviceId } from "@/components/DeviceId";

const NO_GROUP = "none";

const contactSchema = z.object({
  name: contactNameSchema,
  peerOnQId: deviceIdSchema,
  notes: z.string().optional(),
  tags: z.string().optional(),
  groupId: z.string().optional(),
});

type ContactForm = z.infer<typeof contactSchema>;

function parseTags(value?: string): string[] {
  if (!value) return [];
  return Array.from(
    new Set(
      value
        .split(",")
        .map((tag) => tag.trim())
        .filter(Boolean)
        .map((tag) => tag.slice(0, 24)),
    ),
  );
}

function ContactCard({ contact, onEdit, onToggleFavorite, onRemove }: {
  contact: Contact;
  onEdit: (contact: Contact) => void;
  onToggleFavorite: (contact: Contact) => void;
  onRemove: (contact: Contact) => void;
}) {
  const tags = contact.tags ?? [];
  return (
    <div className="p-4 rounded-xl border bg-background hover:border-primary/30 transition-all flex items-start justify-between group">
      <div className="min-w-0">
        <h4 className="font-semibold text-foreground mb-1 truncate">{contact.name}</h4>
        <DeviceId id={contact.peerOnQId} className="mb-3 bg-secondary" />
        {contact.notes && <p className="text-xs text-muted-foreground line-clamp-1">{contact.notes}</p>}
        {tags.length > 0 && (
          <div className="flex flex-wrap gap-1 mt-2">
            {tags.map((tag) => (
              <span key={tag} className="px-1.5 py-0.5 rounded bg-secondary text-secondary-foreground text-[10px]">{tag}</span>
            ))}
          </div>
        )}
        {contact.isPrototypeRecord && (
          <span className="inline-block mt-2 px-1.5 py-0.5 rounded bg-primary/10 text-primary text-[10px] uppercase font-bold">
            Local Record
          </span>
        )}
      </div>
      <div className="flex flex-col gap-1 items-end opacity-100 sm:opacity-0 sm:group-hover:opacity-100 focus-within:opacity-100 transition-opacity">
        <Button variant="ghost" size="icon" className="h-8 w-8" onClick={() => onToggleFavorite(contact)}
          aria-label={contact.isFavorite ? "Remove from favorites" : "Add to favorites"}>
          <Star className={`h-4 w-4 ${contact.isFavorite ? "fill-warning text-warning" : "text-muted-foreground"}`} />
        </Button>
        <DropdownMenu>
          <DropdownMenuTrigger asChild>
            <Button variant="ghost" size="icon" className="h-8 w-8" aria-label={`Actions for ${contact.name}`}>
              <MoreVertical className="h-4 w-4 text-muted-foreground" />
            </Button>
          </DropdownMenuTrigger>
          <DropdownMenuContent align="end">
            <DropdownMenuItem onClick={() => onEdit(contact)}><Pencil className="mr-2 h-4 w-4" /> Edit</DropdownMenuItem>
            <DropdownMenuItem className="text-destructive focus:text-destructive" onClick={() => onRemove(contact)}>
              <Trash2 className="mr-2 h-4 w-4" /> Remove
            </DropdownMenuItem>
          </DropdownMenuContent>
        </DropdownMenu>
      </div>
    </div>
  );
}

function ContactResults({ contacts, filteredContacts, onAdd, onEdit, onToggleFavorite, onRemove }: {
  contacts: Contact[];
  filteredContacts: Contact[];
  onAdd: () => void;
  onEdit: (contact: Contact) => void;
  onToggleFavorite: (contact: Contact) => void;
  onRemove: (contact: Contact) => void;
}) {
  if (contacts.length === 0) {
    return <EmptyState icon={<BookUser className="h-12 w-12" />} title="Address book is empty"
      description="Add your frequent connections here for quick access." className="h-full border-none"
      action={<Button onClick={onAdd}><Plus className="mr-2 h-4 w-4" /> Add Contact</Button>} />;
  }
  if (filteredContacts.length === 0) {
    return <EmptyState title="No contacts found" description="Try adjusting your search or group filter."
      className="h-full border-none" />;
  }
  return (
    <div className="grid grid-cols-1 xl:grid-cols-2 gap-4 p-4 content-start">
      {filteredContacts.map((contact) => (
        <ContactCard key={contact.id} contact={contact} onEdit={onEdit}
          onToggleFavorite={onToggleFavorite} onRemove={onRemove} />
      ))}
    </div>
  );
}

function filterContacts(contacts: Contact[], search: string, activeGroup: string): Contact[] {
  const query = search.toLowerCase();
  return contacts.filter((contact) => {
    const values = [contact.name, contact.peerOnQId, ...(contact.tags ?? [])];
    const matchesSearch = values.some((value) => value.toLowerCase().includes(query));
    if (activeGroup === "all") return matchesSearch;
    if (activeGroup === "favorites") return matchesSearch && contact.isFavorite;
    return matchesSearch && contact.groupId === activeGroup;
  });
}

function GroupList({ groups, activeGroup, onSelect }: {
  groups: ContactGroup[];
  activeGroup: string;
  onSelect(groupId: string): void;
}) {
  if (groups.length === 0) return <div className="px-3 py-2 text-xs text-muted-foreground italic">No groups created</div>;
  return <>{groups.map((group) => (
    <button key={group.id} onClick={() => onSelect(group.id)}
      className={`w-full flex items-center gap-2 px-3 py-2 rounded-lg text-sm transition-colors ${activeGroup === group.id ? "bg-primary/10 text-primary font-medium" : "hover:bg-secondary text-foreground"}`}>
      <Folder className="h-4 w-4 text-muted-foreground" /> {group.name}
    </button>
  ))}</>;
}

function initialContactGroup(activeGroup: string): string {
  return activeGroup === "all" || activeGroup === "favorites" ? NO_GROUP : activeGroup;
}

function normalizedGroup(groupId?: string): string | undefined {
  return groupId && groupId !== NO_GROUP ? groupId : undefined;
}

function contactFromForm(values: ContactForm, editing: Contact | null, activeGroup: string): Contact {
  const fields = {
    name: values.name,
    peerOnQId: values.peerOnQId,
    notes: values.notes,
    tags: parseTags(values.tags),
    groupId: normalizedGroup(values.groupId),
  };
  if (editing) return { ...editing, ...fields };
  return {
    id: crypto.randomUUID(),
    ...fields,
    isPrototypeRecord: true,
    isFavorite: activeGroup === "favorites",
  };
}

function contactDialogCopy(editing: Contact | null) {
  return editing
    ? { title: "Edit Contact", description: "Update this locally stored contact.", submit: "Save Changes" }
    : { title: "Add Contact", description: "Save a remote device to your address book.", submit: "Save Contact" };
}

function groupButtonClass(active: boolean): string {
  return `w-full flex items-center justify-between px-3 py-2 rounded-lg text-sm transition-colors ${active ? "bg-primary/10 text-primary font-medium" : "hover:bg-secondary text-foreground"}`;
}

export function AddressBookPage() {
  const [contacts, setContacts] = useState<Contact[]>([]);
  const [groups, setGroups] = useState<ContactGroup[]>([]);
  const [search, setSearch] = useState("");
  const [activeGroup, setActiveGroup] = useState<string>("all");
  const [contactDialogOpen, setContactDialogOpen] = useState(false);
  const [editingContact, setEditingContact] = useState<Contact | null>(null);
  const [contactToRemove, setContactToRemove] = useState<Contact | null>(null);
  const [groupDialogOpen, setGroupDialogOpen] = useState(false);
  const [groupName, setGroupName] = useState("");

  useEffect(() => {
    loadData();
  }, []);

  async function loadData() {
    setContacts(await contactRepository.listContacts());
    setGroups(await contactRepository.listGroups());
  }

  const form = useForm<ContactForm>({
    resolver: zodResolver(contactSchema),
    defaultValues: { name: "", peerOnQId: "", notes: "", tags: "", groupId: NO_GROUP },
  });

  const openAddDialog = () => {
    setEditingContact(null);
    form.reset({
      name: "",
      peerOnQId: "",
      notes: "",
      tags: "",
      groupId: initialContactGroup(activeGroup),
    });
    setContactDialogOpen(true);
  };

  const openEditDialog = (contact: Contact) => {
    setEditingContact(contact);
    form.reset({
      name: contact.name,
      peerOnQId: contact.peerOnQId,
      notes: contact.notes ?? "",
      tags: (contact.tags ?? []).join(", "),
      groupId: contact.groupId ?? NO_GROUP,
    });
    setContactDialogOpen(true);
  };

  const onSubmit = async (values: ContactForm) => {
    const contact = contactFromForm(values, editingContact, activeGroup);

    await contactRepository.saveContact(contact);
    await loadData();
    setContactDialogOpen(false);
    setEditingContact(null);
    form.reset();
  };

  const toggleFavorite = async (contact: Contact) => {
    await contactRepository.saveContact({ ...contact, isFavorite: !contact.isFavorite });
    await loadData();
  };

  const removeContact = async (contact: Contact) => {
    await contactRepository.removeContact(contact.id);
    await loadData();
    setContactToRemove(null);
  };

  const addGroup = async () => {
    const name = groupName.trim();
    if (!name) return;
    await contactRepository.saveGroup({ id: crypto.randomUUID(), name });
    await loadData();
    setGroupName("");
    setGroupDialogOpen(false);
  };

  const filteredContacts = filterContacts(contacts, search, activeGroup);
  const dialogCopy = contactDialogCopy(editingContact);

  return (
    <div className="space-y-6 pb-8 h-full flex flex-col">
      <PageHeader
        title="Address Book"
        description="Organize your frequent connections and clients."
        action={
          <Button onClick={openAddDialog} data-testid="button-add-contact">
            <Plus className="mr-2 h-4 w-4" /> Add Contact
          </Button>
        }
      />

      <Dialog open={contactDialogOpen} onOpenChange={setContactDialogOpen}>
        <DialogContent>
          <DialogHeader>
            <DialogTitle>{dialogCopy.title}</DialogTitle>
            <DialogDescription>{dialogCopy.description}</DialogDescription>
          </DialogHeader>
          <Form {...form}>
            <form onSubmit={form.handleSubmit(onSubmit)} className="space-y-4 pt-4">
              <FormField control={form.control} name="name" render={({ field }) => (
                <FormItem><FormLabel>Name</FormLabel><FormControl><Input placeholder="John Doe - Laptop" {...field} /></FormControl><FormMessage /></FormItem>
              )} />
              <FormField control={form.control} name="peerOnQId" render={({ field }) => (
                <FormItem><FormLabel>PeerOnQ ID</FormLabel><FormControl><Input placeholder="000-000-000-000" className="font-device-id" {...field} /></FormControl><FormMessage /></FormItem>
              )} />
              <FormField control={form.control} name="groupId" render={({ field }) => (
                <FormItem>
                  <FormLabel>Group</FormLabel>
                  <Select onValueChange={field.onChange} value={field.value}>
                    <FormControl>
                      <SelectTrigger data-testid="select-contact-group">
                        <SelectValue placeholder="No group" />
                      </SelectTrigger>
                    </FormControl>
                    <SelectContent>
                      <SelectItem value={NO_GROUP}>No group</SelectItem>
                      {groups.map((g) => (
                        <SelectItem key={g.id} value={g.id}>{g.name}</SelectItem>
                      ))}
                    </SelectContent>
                  </Select>
                  <FormMessage />
                </FormItem>
              )} />
              <FormField control={form.control} name="tags" render={({ field }) => (
                <FormItem>
                  <FormLabel>Tags (Optional)</FormLabel>
                  <FormControl><Input placeholder="office, client, windows" {...field} /></FormControl>
                  <FormDescription>Separate tags with commas.</FormDescription>
                  <FormMessage />
                </FormItem>
              )} />
              <FormField control={form.control} name="notes" render={({ field }) => (
                <FormItem><FormLabel>Notes (Optional)</FormLabel><FormControl><Input placeholder="Any details..." {...field} /></FormControl><FormMessage /></FormItem>
              )} />
              <DialogFooter className="pt-4">
                <Button type="submit">{dialogCopy.submit}</Button>
              </DialogFooter>
            </form>
          </Form>
        </DialogContent>
      </Dialog>

      <Dialog open={groupDialogOpen} onOpenChange={setGroupDialogOpen}>
        <DialogContent className="sm:max-w-sm">
          <DialogHeader>
            <DialogTitle>New Group</DialogTitle>
            <DialogDescription>Groups are stored locally in this browser.</DialogDescription>
          </DialogHeader>
          <Input
            placeholder="Group name"
            value={groupName}
            onChange={(e) => setGroupName(e.target.value)}
            onKeyDown={(e) => e.key === "Enter" && addGroup()}
            data-testid="input-group-name"
          />
          <DialogFooter>
            <Button onClick={addGroup} disabled={!groupName.trim()}>Create Group</Button>
          </DialogFooter>
        </DialogContent>
      </Dialog>

      <div className="flex flex-col md:flex-row gap-6 flex-1 min-h-[500px]">
        {/* Sidebar */}
        <div className="w-full md:w-64 shrink-0 space-y-4">
          <div className="relative">
            <Search className="absolute left-3 top-1/2 -translate-y-1/2 h-4 w-4 text-muted-foreground" />
            <Input
              placeholder="Search contacts..."
              value={search}
              onChange={(e) => setSearch(e.target.value)}
              className="pl-9 bg-card border"
            />
          </div>

          <div className="bg-card border rounded-xl overflow-hidden">
            <div className="p-2 space-y-1">
              <button
                onClick={() => setActiveGroup("all")}
                className={groupButtonClass(activeGroup === "all")}
              >
                <div className="flex items-center gap-2"><Users className="h-4 w-4" /> All Contacts</div>
                <span className="text-xs bg-background px-1.5 py-0.5 rounded-md">{contacts.length}</span>
              </button>
              <button
                onClick={() => setActiveGroup("favorites")}
                className={groupButtonClass(activeGroup === "favorites")}
              >
                <div className="flex items-center gap-2"><Star className="h-4 w-4" /> Favorites</div>
                <span className="text-xs bg-background px-1.5 py-0.5 rounded-md">{contacts.filter(c => c.isFavorite).length}</span>
              </button>
            </div>

            <div className="p-2 border-t bg-muted/20 space-y-1">
              <div className="px-3 py-2 text-xs font-semibold text-muted-foreground uppercase tracking-wider flex justify-between items-center">
                Groups
                <Button
                  variant="ghost"
                  size="icon"
                  className="h-5 w-5 rounded-full"
                  onClick={() => setGroupDialogOpen(true)}
                  aria-label="Create group"
                  data-testid="button-add-group"
                >
                  <Plus className="h-3 w-3" />
                </Button>
              </div>
              <GroupList groups={groups} activeGroup={activeGroup} onSelect={setActiveGroup} />
            </div>
          </div>
        </div>

        {/* Main Content */}
        <div className="flex-1 bg-card border rounded-xl overflow-hidden flex flex-col">
          <ContactResults contacts={contacts} filteredContacts={filteredContacts} onAdd={openAddDialog}
            onEdit={openEditDialog} onToggleFavorite={toggleFavorite} onRemove={setContactToRemove} />
        </div>
      </div>

      <ConfirmDialog
        open={contactToRemove !== null}
        onOpenChange={(open) => !open && setContactToRemove(null)}
        title="Remove this contact?"
        description={`"${contactToRemove?.name ?? ""}" will be deleted from this browser. Local prototype records cannot be recovered.`}
        confirmLabel="Remove"
        variant="destructive"
        onConfirm={() => contactToRemove && removeContact(contactToRemove)}
      />
    </div>
  );
}
